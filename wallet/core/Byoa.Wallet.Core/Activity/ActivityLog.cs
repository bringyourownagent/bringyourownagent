using System.Text.Json;
using System.Text.Json.Serialization;

namespace Byoa.Wallet.Activity;

/// <summary>
/// One line of the activity log: a request, a call or a release. Calls carry the source that answered, the
/// fallback route when one happened, and the cost. The content (audio, the transcript) is never logged.
/// </summary>
public sealed class ActivityEntry
{
    public DateTimeOffset At { get; set; }
    public string Origin { get; set; } = "";

    /// <summary>"request", "call", "release", or "link" (the direct transport: a link, its connection).</summary>
    public string Kind { get; set; } = "";

    /// <summary>"link" when it came through a bringyourownagent:// link and the wallet's own connection to the site; null for the extension.</summary>
    public string? Via { get; set; }

    public string? Skill { get; set; }

    /// <summary>
    /// For requests: allowed, auto, declined, blocked, held (no click), invalid, closed (no answer in time).
    /// For calls: ok, failed, limit, refused (a paid fallback the visitor declined), invalid.
    /// For links: invalid, blocked, ignored, failed, connected, closed, error.
    /// </summary>
    public string Outcome { get; set; } = "";

    /// <summary>The source that answered (calls).</summary>
    public string? Source { get; set; }

    /// <summary>The fallback route, when the first choice did not answer: "GigaAM v3: turned off → Whisper".</summary>
    public string? Fallback { get; set; }

    /// <summary>Spec money, "0 USD" for a local source.</summary>
    public string Cost { get; set; } = "0 USD";

    public double? ClipSeconds { get; set; }
    public string? Language { get; set; }
    public int? Milliseconds { get; set; }

    /// <summary>Gate reason codes or a short note; never content.</summary>
    public string? Note { get; set; }

    [JsonIgnore]
    public decimal CostValue => Money.Parse(Cost, 0);
}

/// <summary>
/// The activity log: the recent entries in memory, and one JSON line per entry in
/// <c>activity-YYYY-MM-DD.jsonl</c> in the data folder (a new file each day; old files are left alone).
/// </summary>
public sealed class ActivityLog
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    const int Keep = 5000;

    readonly string? _folder;
    readonly object _gate = new();
    readonly List<ActivityEntry> _entries = [];

    // Daily totals of calls that ran, per local day and origin, kept apart from the capped list of entries.
    readonly Dictionary<(DateTime Day, string Origin), (decimal Spent, int Calls)> _daily = [];

    public event Action<ActivityEntry>? Added;

    /// <param name="folder">The data folder; null keeps the log in memory only (tests).</param>
    public ActivityLog(string? folder, DateTimeOffset now)
    {
        _folder = folder;
        if (folder is null || !Directory.Exists(folder)) return;
        // The files are read back, newest day first, until the list is full: today's file always whole, so that the daily
        // limits survive a restart of the wallet, and the days before it for the Activity page.
        var today = Path.GetFileName(FileFor(now));
        var days = new List<List<ActivityEntry>>();
        var read = 0;
        foreach (var file in Directory.GetFiles(folder, "activity-*.jsonl")
                     .Where(f => string.CompareOrdinal(Path.GetFileName(f), today) <= 0)
                     .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            if (read >= Keep) break;
            var day = ReadFile(file);
            days.Add(day);
            read += day.Count;
        }

        for (var i = days.Count - 1; i >= 0; i--)
        {
            foreach (var e in days[i])
            {
                _entries.Add(e);
                Count(e);
            }
        }

        if (_entries.Count > Keep) _entries.RemoveRange(0, _entries.Count - Keep);
    }

    static List<ActivityEntry> ReadFile(string file)
    {
        var entries = new List<ActivityEntry>();
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ActivityEntry>(line, Json) is { } e) entries.Add(e);
                }
                catch (JsonException)
                {
                    // skip a damaged line
                }
            }
        }
        catch (IOException)
        {
            // a file another program holds open: its day is simply not shown
        }

        return entries;
    }

    void Count(ActivityEntry e)
    {
        if (e.Kind != "call" || e.Outcome != "ok") return;
        var key = (e.At.ToLocalTime().Date, e.Origin);
        var (spent, calls) = _daily.TryGetValue(key, out var v) ? v : (0m, 0);
        _daily[key] = (spent + e.CostValue, calls + 1);
    }

    string FileFor(DateTimeOffset at) => Path.Combine(_folder!, "activity-" + at.ToLocalTime().ToString("yyyy-MM-dd") + ".jsonl");

    public void Add(ActivityEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Count(entry);
            if (_entries.Count > Keep) _entries.RemoveRange(0, _entries.Count - Keep);
            if (_folder is not null)
            {
                Directory.CreateDirectory(_folder);
                File.AppendAllText(FileFor(entry.At), JsonSerializer.Serialize(entry, Json) + "\n");
            }
        }

        Added?.Invoke(entry);
    }

    /// <summary>The newest entries first.</summary>
    public IReadOnlyList<ActivityEntry> Recent(int count = 500)
    {
        lock (_gate) return _entries.AsEnumerable().Reverse().Take(count).ToList();
    }

    /// <summary>Spend and calls today (local day), for one origin or for every site.</summary>
    public (decimal Spent, int Calls) Today(DateTimeOffset now, string? origin = null)
    {
        var day = now.ToLocalTime().Date;
        lock (_gate)
        {
            decimal spent = 0;
            var calls = 0;
            foreach (var ((d, o), (s, c)) in _daily)
            {
                if (d != day || (origin is not null && o != origin)) continue;
                spent += s;
                calls += c;
            }

            return (spent, calls);
        }
    }
}
