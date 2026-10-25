using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BringYourOwnAgent.Relay;

/// <summary>One message of a call as the page sends it: <c>role</c> is system, user or assistant (CONTRACT.md §3.2).</summary>
public sealed record JobMessage(string? Role, string? Content);

/// <summary>What the page asks the server to forward to its own wallet: one participant answers one question.</summary>
/// <param name="Pairing">The pairing token of this room's wallet connection.</param>
/// <param name="Model">The <c>ref</c> of one participant of the current grant; a call may name nothing else.</param>
/// <param name="Messages">The transcript: at least one message, the last one the question.</param>
/// <param name="MaxTokens">The most tokens the answer may take; clamped to the site's cap and the grant's.</param>
/// <param name="Temperature">0 to 1; clamped.</param>
public sealed record JobRequest(string? Pairing, string? Model, List<JobMessage>? Messages, int? MaxTokens, double? Temperature);

/// <summary>A job that was started: its id, or why it was not (<see cref="WalletJobs.Start"/> lists the words).</summary>
public sealed record JobStarted(string? Id, string? Error);

/// <param name="State">"pending" (ask again), "done" or "unknown" (no such job: finished long ago, or not this room's).</param>
/// <param name="Ok">done and the wallet answered with something readable.</param>
/// <param name="Text">done and ok: the answer, cleaned and cut.</param>
/// <param name="Partial">While pending: the pieces the wallet has streamed so far (a preview; the final answer replaces them).</param>
/// <param name="Reason">done and not ok: "failed" (the wallet refused or the model failed), "timeout" or "gone" (the wallet went away).</param>
public sealed record JobResult(string State, bool Ok = false, string? Text = null, string? Partial = null, string? Reason = null);

/// <summary>
/// The page's road to the visitor's wallet when it is connected without the extension: the page cannot call that wallet itself, so it asks the
/// server, which sends the call down the wallet's own connection and keeps the answer until the page comes for it. A long answer must not hold
/// one request open behind a proxy, so the page asks again every second and reads <see cref="JobResult.Partial"/> while the model writes.
/// </summary>
/// <remarks>
/// The page can ask for exactly one thing — <c>text.generate</c>, for one participant of the current grant, with a bounded transcript — and
/// nothing else reaches the wallet: the ref is checked against the live grant, the roles, the message count, the total characters, <c>maxTokens</c>
/// and <c>temperature</c> are clamped here, and no other parameter (no <c>choices</c>, no <c>language</c>, no <c>thinking</c>) is passed on.
/// <c>stream</c> is always asked for: the direct link only carries it.
/// Everything is in memory; a job is forgotten when it is fetched, when the page stops it, or ten minutes after it began, and no message or answer
/// text is ever logged or written down.
/// </remarks>
public sealed class WalletJobs
{
    /// <summary>The only skill a page may ask for through these jobs (CONTRACT.md §3.2).</summary>
    public const string Skill = "text.generate";

    static readonly TimeSpan Kept = TimeSpan.FromMinutes(10);
    static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { "system", "user", "assistant" };

    sealed class Job(string room, DateTimeOffset began, CancellationTokenSource stop)
    {
        public string Room { get; } = room;
        public DateTimeOffset Began { get; } = began;
        public CancellationTokenSource Stop { get; } = stop;
        public Task<WalletAnswer> Answer { get; set; } = Task.FromResult(new WalletAnswer(WalletOutcome.Cancelled));
        /// <summary>The pieces streamed so far (written by the wallet's connection, read by the page's poll).</summary>
        public volatile string Partial = "";
    }

    readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    readonly DirectHub _hub;
    readonly RelayOptions _options;
    readonly TimeProvider _clock;
    readonly TimeSpan _timeout;
    readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _asked = new(StringComparer.Ordinal);
    readonly object _sweepGate = new();
    DateTimeOffset _swept = DateTimeOffset.MinValue;

    /// <param name="hub">The hub the wallets are connected to.</param>
    /// <param name="options">The site's settings (how long a call may take, how many a page may start).</param>
    /// <param name="clock">The clock a job ages by (a test moves it by hand).</param>
    public WalletJobs(DirectHub hub, RelayOptions options, TimeProvider? clock = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(options.JobSeconds, 1, 600));
    }

    /// <summary>How many jobs are being kept (tests, health).</summary>
    public int Count => _jobs.Count;

    /// <summary>Starts a job; <c>Error</c> says why not: "bad-request" (a shape that is not ours), "offline" (no live wallet behind the pairing),
    /// "no-text" (that wallet was not allowed text), "bad-model" (a ref the grant does not have), "busy" (too many calls at once or in a minute).</summary>
    public JobStarted Start(string room, JobRequest r)
    {
        var now = _clock.GetUtcNow();
        Sweep(now);
        if (!DirectHub.IsRoomId(room) || !ValidMessages(r.Messages)) return new JobStarted(null, "bad-request");
        var channel = _hub.Channel(room, r.Pairing);
        if (channel is null) return new JobStarted(null, "offline");
        if (!channel.Has(Skill)) return new JobStarted(null, "no-text");
        if (!channel.Knows(r.Model)) return new JobStarted(null, "bad-model");

        var limits = channel.Limits;
        var text = r.Messages!.Sum(m => m.Content!.Length);
        if (text > Math.Min(limits.MaxTextChars, Math.Max(1, _options.MaxCallChars))) return new JobStarted(null, "bad-request");
        if (channel.Pending >= limits.MaxParallelCalls) return new JobStarted(null, "busy");
        if (_jobs.Count >= Math.Max(1, _options.MaxJobs) || _jobs.Count(j => j.Value.Room == room) >= Math.Max(1, _options.MaxJobsPerRoom)
            || !Allowed(room, now)) return new JobStarted(null, "busy");

        var ownTokens = Math.Max(1, _options.MaxCallTokens);
        var maxTokens = Math.Clamp(Math.Min(r.MaxTokens ?? Math.Max(1, _options.DefaultCallTokens), ownTokens), 1, Math.Max(1, limits.MaxAnswerTokens));
        var temperature = Math.Clamp(r.Temperature ?? _options.DefaultTemperature, 0, 1);
        var input = new { messages = r.Messages!.Select(m => new { role = m.Role, content = m.Content }).ToArray() };
        var parameters = new { model = r.Model, maxTokens, temperature, stream = true };
        var id = DirectHub.NewSecret();
        var stop = new CancellationTokenSource();
        var job = new Job(room, now, stop);
        // the wait is the site's own (300 s by default) but never longer than the grant allows for one call
        var wait = TimeSpan.FromSeconds(Math.Min(_timeout.TotalSeconds, limits.MaxCallSeconds));
        var maxChars = Math.Max(1, _options.MaxAnswerChars);
        job.Answer = channel.CallAsync(Skill, input, parameters, maxChars, wait, stop.Token, piece =>
            job.Partial = piece.Length <= maxChars ? piece : piece[..maxChars]);
        _jobs[id] = job;
        return new JobStarted(id, null);
    }

    /// <summary>The answer when it is there, else (after at most <paramref name="wait"/>) "pending" with the streamed pieces so far.
    /// A finished job is handed out once. The page asks every second, so the route passes no wait at all.</summary>
    public async Task<JobResult> PollAsync(string room, string? id, TimeSpan wait, CancellationToken ct)
    {
        Sweep(_clock.GetUtcNow(), often: true);
        if (!DirectHub.IsRoomId(room) || id is null || !_jobs.TryGetValue(id, out var job) || job.Room != room) return new JobResult("unknown");
        try { await job.Answer.WaitAsync(wait, ct); }
        catch (TimeoutException) { return new JobResult("pending", Partial: job.Partial); }
        catch (OperationCanceledException) { return new JobResult("pending", Partial: job.Partial); }
        if (!_jobs.TryRemove(id, out _)) return new JobResult("unknown");
        job.Stop.Dispose();
        var a = job.Answer.Result;
        return a.Outcome switch
        {
            WalletOutcome.Text when a.Text.Length > 0 => new JobResult("done", true, a.Text),
            WalletOutcome.TimedOut => new JobResult("done", false, null, null, "timeout"),
            WalletOutcome.Cancelled => new JobResult("done", false, null, null, "gone"),
            _ => new JobResult("done", false, null, null, "failed"),
        };
    }

    /// <summary>The page stops waiting for a job: it is forgotten at once and a late result is ignored. The wallet is not told
    /// (the protocol has no cancel frame); it may still finish the call, and the answer is then dropped.</summary>
    public bool Cancel(string room, string? id)
    {
        // the room is checked before anything is removed: another room's page must not be able to stop this room's job
        if (!DirectHub.IsRoomId(room) || id is null || !_jobs.TryGetValue(id, out var job) || job.Room != room) return false;
        if (!_jobs.TryRemove(id, out _)) return false;
        job.Stop.Cancel();
        job.Stop.Dispose();
        return true;
    }

    /// <summary>Messages of one call: the contract's three roles, at least one, the last one the visitor's or the site's question,
    /// no empty text, at most <see cref="RelayOptions.MaxCallMessages"/>. The character budget is checked separately against the grant.</summary>
    bool ValidMessages(List<JobMessage>? messages)
    {
        if (messages is not { Count: > 0 } || messages.Count > Math.Max(1, _options.MaxCallMessages)) return false;
        var total = 0;
        foreach (var m in messages)
        {
            if (m.Role is null || !Roles.Contains(m.Role) || string.IsNullOrWhiteSpace(m.Content)) return false;
            total += m.Content.Length;
            if (total > Math.Max(1, _options.MaxCallChars)) return false;
        }
        return messages[^1].Role == "user";
    }

    bool Allowed(string room, DateTimeOffset now)
    {
        var times = _asked.GetOrAdd(room, _ => new Queue<DateTimeOffset>());
        lock (times)
        {
            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromMinutes(1)) times.Dequeue();
            if (times.Count >= Math.Max(1, _options.JobsPerMinute)) return false;
            times.Enqueue(now);
            return true;
        }
    }

    /// <param name="now">The time to sweep by.</param>
    /// <param name="often">The poll route asks every second: the sweep runs at most once every thirty seconds there, so a page that
    /// asks again and again does not walk the whole table each time. Starting a job always sweeps.</param>
    void Sweep(DateTimeOffset now, bool often = false)
    {
        lock (_sweepGate)
        {
            if (often && now - _swept < TimeSpan.FromSeconds(30)) return;
            _swept = now;
        }
        foreach (var (id, job) in _jobs)
            if (now - job.Began > Kept && _jobs.TryRemove(id, out var gone))
            {
                gone.Stop.Cancel();                     // the wallet's late answer is dropped, and the call stops waiting
                gone.Stop.Dispose();
            }
        foreach (var (room, times) in _asked)
            lock (times)
                if (times.Count == 0 || now - times.Peek() > TimeSpan.FromMinutes(2)) _asked.TryRemove(room, out _);
    }
}