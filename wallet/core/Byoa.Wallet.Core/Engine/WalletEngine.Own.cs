using System.Diagnostics;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Audio;
using Byoa.Wallet.Everywhere;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>
/// The wallet's own calls: the visitor's dictation and the check of his selected text (see <see cref="EverywhereSettings"/>). No website asks, so there
/// is no consent, no grant and no site record; but the call goes through exactly what a site's call goes through after its grant: the gate, the daily
/// limits (it counts as a site called "Everywhere": its calls and its money are in the totals and in the Activity page), the visitor's routes with
/// their fallbacks, "never start a model for me", and one entry in the activity log. The entry never holds the audio or any text.
/// By default it runs on models on this computer or a home server, and the text check may also use a free demo service (cloud, no key, no cost) after
/// them while "free cloud" is on; it never uses a paid source unless the visitor allowed a paid fallback on the Everywhere page, and then the wallet asks
/// before each paid call, as it does for a site. Dictation (sound) never leaves the computer unless a paid fallback is allowed. With "free cloud" off
/// and no paid fallback the manifest says <c>local-only</c>.
/// The text route never holds an agent: <c>PlainTextSources</c> leaves them out.
/// </summary>
public sealed partial class WalletEngine
{
    /// <summary>The name the wallet's own calls have in the activity log and in the daily totals.</summary>
    public const string OwnOrigin = "Everywhere";

    const string OwnVia = "wallet";

    // The check is a thing the visitor waits for: a model that has not answered in this long is not going to.
    const int OwnTextCallSeconds = 90;
    const int OwnTextAnswerTokens = 8000;
    const double OwnTextTemperature = 0.3;

    IOwnCalls? _ownCalls;

    /// <summary>The wallet's own calls as <see cref="IOwnCalls"/>, for dictation and the check.</summary>
    public IOwnCalls Own => _ownCalls ??= new OwnCallsAdapter(this);

    sealed class OwnCallsAdapter(WalletEngine engine) : IOwnCalls
    {
        public Task<OwnResult> TranscribeAsync(float[] samples, string? language, CancellationToken ct) => engine.TranscribeOwnAsync(samples, language, ct);

        public Task<OwnResult> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct) => engine.GenerateOwnAsync(messages, ct);
    }

    /// <summary>Recognises a recording (16 kHz mono) by the visitor's speech route.</summary>
    public async Task<OwnResult> TranscribeOwnAsync(float[] samples, string? language, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        language = Vocabulary.PrimaryLanguage(language);
        var clip = new WavClip(samples, Wav.TargetRate, 1, 16);
        var seconds = Math.Round(clip.Seconds, 3);
        var entry = new ActivityEntry
        {
            Origin = OwnOrigin, Kind = Wire.Call, Via = OwnVia, Skill = Vocabulary.Transcribe, ClipSeconds = seconds, Language = language,
        };
        if (samples.Length == 0) return OwnFailure(entry, Vocabulary.Transcribe, "invalid", "nothing was recorded");

        var settings = _store.Snapshot();
        var own = OwnLimits(settings, Vocabulary.Transcribe, maxClipSeconds: (int)Math.Ceiling(seconds), maxTextChars: null);
        var sources = settings.Everywhere.AllowPaid ? _sources : _sources.Where(s => !s.Paid).ToList();
        var hold = new DailyHold(OwnOrigin);
        if (!HoldCall(hold, own.CallsPerDay)) return OwnFailure(entry, Vocabulary.Transcribe, "limit", "calls per day");
        try
        {
            var result = await RouteAsync(entry, own.Session, own.Site, Vocabulary.Transcribe, clip, seconds, language, own.SitePerDay, own.WalletPerDay, hold,
                started, ct, sourceOverride: sources).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return OwnResultOf(result, Vocabulary.Transcribe, entry, language);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogCancelled(entry);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return OwnFailure(entry, Vocabulary.Transcribe, "failed", e.GetType().Name);
        }
        finally
        {
            ReleaseDaily(hold);
        }
    }

    /// <summary>Asks the first text model of the visitor's route that answers (never an agent). The messages are the wallet's own, so they are not logged.</summary>
    public async Task<OwnResult> GenerateOwnAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var entry = new ActivityEntry { Origin = OwnOrigin, Kind = Wire.Call, Via = OwnVia, Skill = Vocabulary.Generate };
        var chars = messages.Sum(m => m.Content.Length);
        if (messages.Count == 0 || chars == 0) return OwnFailure(entry, Vocabulary.Generate, "invalid", "no text");

        var settings = _store.Snapshot();
        var own = OwnLimits(settings, Vocabulary.Generate, maxClipSeconds: null, maxTextChars: chars);
        var limits = own.Session.Limits ?? TextLimits.Defaults;
        if (chars > limits.InputChars) return OwnFailure(entry, Vocabulary.Generate, "invalid", "text too long for the wallet: at most " + limits.InputChars + " characters");
        // Never a paid source unless he allowed it; a free demo service (cloud, no key, no cost) only while "free cloud" is on.
        var sources = settings.Everywhere.AllowPaid ? PlainTextSources(settings)
            : PlainTextSources(settings).Where(s => !s.Paid && (settings.Everywhere.AllowFreeCloud || s.Runs != Byoa.Wallet.Sources.Runs.ApiKey)).ToList();
        var hold = new DailyHold(OwnOrigin);
        if (!HoldCall(hold, own.CallsPerDay)) return OwnFailure(entry, Vocabulary.Generate, "limit", "calls per day");
        try
        {
            var result = await RouteTextAsync(entry, own.Session, own.Site, messages, chars, limits.AnswerTokens, OwnTextTemperature, null, null,
                own.CallsPerDay, own.SitePerDay, own.WalletPerDay, hold, started, ct, sourceOverride: sources, thinking: false).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return OwnResultOf(result, Vocabulary.Generate, entry, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogCancelled(entry);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return OwnFailure(entry, Vocabulary.Generate, "failed", e.GetType().Name);
        }
        finally
        {
            ReleaseDaily(hold);
        }
    }

    /// <summary>The session, the site record and the daily limits of one own call: a session of its own (nothing carries over), limits of a default site.</summary>
    (GrantSession Session, SiteRecord Site, int CallsPerDay, decimal SitePerDay, decimal WalletPerDay) OwnLimits(
        WalletSettings settings, string skill, int? maxClipSeconds, int? maxTextChars)
    {
        var sitePerDay = Money.Parse(settings.Limits.SitePerDay, 0.20m);
        var walletPerDay = Money.Parse(settings.Limits.WalletPerDay, 1.00m);
        // Sound never goes to a service on the quiet; a text may go to a free demo service while "free cloud" is on (it never goes to a paid one unasked).
        var localOnly = skill == Vocabulary.Generate ? !(settings.Everywhere.AllowPaid || settings.Everywhere.AllowFreeCloud) : !settings.Everywhere.AllowPaid;
        var manifest = new JsonObject
        {
            ["byoa"] = "0.1-draft",
            ["purpose"] = new JsonObject { ["summary"] = "The wallet's own dictation and text check", ["limits"] = OwnManifestLimits(maxClipSeconds, maxTextChars) },
            ["skills"] = new JsonObject { [skill] = new JsonObject { ["required"] = true } },
            ["privacy"] = localOnly ? "local-only" : "personal-voice",
        };
        var session = new GrantSession(OwnOrigin, RequestedManifest.From(manifest), [skill], Math.Max(sitePerDay, 0.01m), Now);
        var global = TextLimits.Global(settings.Limits);
        session.Limits = global with
        {
            CallSeconds = Math.Min(global.CallSeconds, OwnTextCallSeconds),
            AnswerTokens = Math.Min(global.AnswerTokens, OwnTextAnswerTokens),
        };
        var site = new SiteRecord
        {
            Origin = OwnOrigin, Status = SiteStatus.Allowed, PaidAllowed = settings.Everywhere.AllowPaid && settings.Everywhere.PaidWithoutAsking,
            Budget = Money.ToSpec(session.Budget), PerDay = settings.Limits.SitePerDay, CallsPerDay = settings.Limits.SiteCallsPerDay,
        };
        return (session, site, settings.Limits.SiteCallsPerDay, sitePerDay, walletPerDay);
    }

    static JsonObject OwnManifestLimits(int? maxClipSeconds, int? maxTextChars)
    {
        var limits = new JsonObject { ["maxCallsPerMinute"] = 60 };
        if (maxClipSeconds is { } clip) limits["maxClipSeconds"] = Math.Max(clip, 1);
        if (maxTextChars is { } text) limits["maxTextChars"] = Math.Max(text, 1);
        return limits;
    }

    /// <summary>
    /// The visitor cancelled (Esc, a closed popup). The route ends a text call that was cancelled as a failed call and has written its entry then; a
    /// speech call that was cancelled throws before it wrote one. Either way the call that ran is in the log once.
    /// </summary>
    void LogCancelled(ActivityEntry entry)
    {
        if (entry.Outcome.Length > 0) return;
        entry.Outcome = "failed";
        entry.Note = "cancelled";
        Log(entry);
    }

    OwnResult OwnFailure(ActivityEntry entry, string skill, string outcome, string note)
    {
        entry.Outcome = outcome;
        entry.Note = note;
        Log(entry);
        return OwnResult.Failure(PlainOwnFailure(skill, entry, null));
    }

    /// <summary>Turns what the route returned (and what it wrote into the activity entry) into the result the visitor's features read.</summary>
    OwnResult OwnResultOf(JsonNode? result, string skill, ActivityEntry entry, string? language)
    {
        if (result is JsonObject { } ok && ok["ok"]?.GetValue<bool>() == true && ok["output"]?["text"]?.GetValue<string>() is { } text)
            return OwnResult.Success(text);
        return OwnResult.Failure(PlainOwnFailure(skill, entry, language));
    }

    /// <summary>
    /// What went wrong, in words for the visitor: not the gate's codes. The route's own reasons ("Llama · Qwen3: not running") are kept, since they
    /// are what he can act on.
    /// </summary>
    internal static string PlainOwnFailure(string skill, ActivityEntry entry, string? language)
    {
        var what = skill == Vocabulary.Transcribe ? "speech model" : "text model";
        var route = string.IsNullOrWhiteSpace(entry.Fallback) ? "" : " " + entry.Fallback + ".";
        var note = entry.Note ?? "";
        return note switch
        {
            "no source for this language" => language is null
                ? "No speech model that takes any language is set up (or none runs on this computer). Add one in My models, or choose a language on the Everywhere page."
                : "None of your speech models is set up for " + language + ". Add one in My models, or set the language to automatic on the Everywhere page.",
            "no source can do text.generate" => "No text model is set up. Add one in My models.",
            "no source answered" => "No " + what + " answered." + route,
            "money per day" or "calls per day" or "budget per session" or "calls per day for this site" => "The wallet reached today's limit for Everywhere (see Limits)." + route,
            "text route timed out" => "The " + what + " took too long." + route,
            "paid fallback not allowed" => "A paid source was the only one left and you said no.",
            "nothing was recorded" => "Nothing was recorded.",
            _ when entry.Outcome == "refused" => "The wallet refused this call." + route,
            _ when note.StartsWith("text too long", StringComparison.Ordinal) => "The text is too long for the wallet's limits.",
            _ => "The " + what + " did not answer (" + (note.Length == 0 ? entry.Outcome : note) + ")." + route,
        };
    }

    /// <summary>"Always" for a paid call is the visitor's answer for a site, or for Everywhere, which has no site record to keep it in.</summary>
    void RememberPaid(string origin)
    {
        if (origin == OwnOrigin) _store.Update(s => s.Everywhere.PaidWithoutAsking = true);
        else _store.Update(s => Site(s, origin).PaidAllowed = true);
    }
}
