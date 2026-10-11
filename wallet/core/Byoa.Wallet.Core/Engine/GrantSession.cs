using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Byoa.Wallet.Engine;

/// <summary>
/// One granted session: what a page got from <c>byoa.request</c>. It lives in memory only (a restart of the wallet
/// ends it: the page's calls then fail and it asks again). Its spend and call times are the gate's "state".
/// </summary>
public sealed class GrantSession
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public GrantSession(string origin, RequestedManifest manifest, IReadOnlyList<string> skills, decimal budget, DateTimeOffset now)
    {
        Id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        Origin = origin;
        Manifest = manifest;
        Skills = skills;
        Budget = budget;
        ExpiresAt = now + Lifetime;
    }

    public string Id { get; }
    public string Origin { get; }
    public RequestedManifest Manifest { get; }
    public IReadOnlyList<string> Skills { get; }
    public decimal Budget { get; }
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Serializes the gate checks of this grant (spec: "Concurrent calls").</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Money spent and reserved in this session.</summary>
    public decimal Spent { get; set; }

    /// <summary>Times of calls that ran or are running (the last minute is enough for the rate limit).</summary>
    public List<DateTimeOffset> CallTimes { get; } = [];

    /// <summary>The visitor was already told that this session reached a limit.</summary>
    public bool LimitNoticeShown { get; set; }

    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The grant document of spec/rules-0.1-draft.md §4.</summary>
    public string GrantJson()
    {
        var skills = new JsonArray();
        foreach (var s in Skills) skills.Add(s);
        return new JsonObject
        {
            ["byoa"] = "0.1-draft",
            ["skills"] = skills,
            ["budget"] = new JsonObject { ["perSession"] = Money.ToSpec(Budget) },
            ["expiresAt"] = Time(ExpiresAt),
        }.ToJsonString();
    }

    /// <summary>The state document: spend and the call times of the last minute.</summary>
    public string StateJson(DateTimeOffset now)
    {
        CallTimes.RemoveAll(t => t <= now - TimeSpan.FromSeconds(61));
        var times = new JsonArray();
        foreach (var t in CallTimes) times.Add(Time(t));
        return new JsonObject { ["spent"] = Money.ToSpec(Spent), ["callTimes"] = times }.ToJsonString();
    }

    /// <summary>What the page gets: <c>{ grantId, skills, expiresAt }</c>.</summary>
    public JsonObject ToResult()
    {
        var skills = new JsonArray();
        foreach (var s in Skills) skills.Add(s);
        return new JsonObject { ["grantId"] = Id, ["skills"] = skills, ["expiresAt"] = Time(ExpiresAt) };
    }
}
