using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Engine;

/// <summary>
/// One model a site was given for a skill. A site calls it by <paramref name="Ref"/> and never by a source; the
/// <paramref name="SourceIds"/> are the roads to the same model (the visitor's choice first), tried in order.
/// </summary>
/// <param name="ModelId">The canonical name; shown to the site only when it asked and the visitor allows.</param>
public sealed record GrantedModel(string Ref, IReadOnlyList<string> SourceIds, string Name, string ModelId, string Runs, bool Paid);

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

    /// <summary>What the site may use for a call now (set by the engine at each call); the gate sees it as the grant's limits.</summary>
    public TextLimitSet? Limits { get; set; }

    /// <summary>The models the site was given, per skill, in the visitor's order; empty for a site that asked for none (it gets the skill, not models).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GrantedModel>> Models { get; set; } = new Dictionary<string, IReadOnlyList<GrantedModel>>();

    /// <summary>The names of the models go to the site (it asked, and the visitor allows). When not, the site sees "Model A", "Model B".</summary>
    public bool ShowNames { get; set; }

    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The grant document of spec/rules-0.1-draft.md §4.</summary>
    public string GrantJson()
    {
        var skills = new JsonArray();
        foreach (var s in Skills) skills.Add(s);
        var grant = new JsonObject
        {
            ["byoa"] = "0.1-draft",
            ["skills"] = skills,
            ["budget"] = new JsonObject { ["perSession"] = Money.ToSpec(Budget) },
            ["expiresAt"] = Time(ExpiresAt),
        };
        // The visitor's own limits, in the draft rules' names; the stricter of the manifest's and these applies.
        if (Limits is { } limits)
        {
            grant["limits"] = new JsonObject { ["maxTextChars"] = limits.InputChars, ["maxCallsPerMinute"] = limits.CallsPerMinute };
        }

        return grant.ToJsonString();
    }

    /// <summary>The state document: spend and the call times of the last minute.</summary>
    public string StateJson(DateTimeOffset now)
    {
        CallTimes.RemoveAll(t => t <= now - TimeSpan.FromSeconds(61));
        var times = new JsonArray();
        foreach (var t in CallTimes) times.Add(Time(t));
        return new JsonObject { ["spent"] = Money.ToSpec(Spent), ["callTimes"] = times }.ToJsonString();
    }

    /// <summary>
    /// What the page gets: <c>{ grantId, skills, expiresAt }</c>, and for a site that works with text also its limits
    /// (<c>limits</c>) and, if it asked for several models, the models it was given (<c>models</c>).
    /// </summary>
    public JsonObject ToResult()
    {
        var skills = new JsonArray();
        foreach (var s in Skills) skills.Add(s);
        var result = new JsonObject { ["grantId"] = Id, ["skills"] = skills, ["expiresAt"] = Time(ExpiresAt) };
        if (Limits is not null && Skills.Contains(Sources.Vocabulary.Generate)) result["limits"] = LimitsJson();
        if (ModelsJson() is { } models) result["models"] = models;
        return result;
    }

    /// <summary>The limits as the site reads them, in the manifest's names.</summary>
    public JsonObject LimitsJson()
    {
        var limits = Limits ?? TextLimits.Defaults;
        var declared = Manifest.Limits;
        return new JsonObject
        {
            // What the site is held to: the visitor's limit, or the one the site declared if that is lower (the gate holds it to both).
            ["maxTextChars"] = Math.Min(limits.InputChars, declared?.InputChars ?? int.MaxValue),
            ["maxAnswerTokens"] = limits.AnswerTokens,
            ["maxCallSeconds"] = limits.CallSeconds,
            ["maxParallelCalls"] = limits.ParallelCalls,
            ["maxCallsPerMinute"] = Math.Min(limits.CallsPerMinute, declared?.CallsPerMinute ?? int.MaxValue),
        };
    }

    /// <summary>The models of every skill that has some, as <c>{ "text.generate": [ { ref, name, id, runs } ] }</c>; null when no skill has any.</summary>
    public JsonObject? ModelsJson()
    {
        if (Models.Count == 0 || Models.All(m => m.Value.Count == 0)) return null;
        var all = new JsonObject();
        foreach (var (skill, models) in Models)
        {
            if (models.Count == 0) continue;
            var array = new JsonArray();
            for (var i = 0; i < models.Count; i++)
            {
                var m = models[i];
                array.Add(new JsonObject
                {
                    ["ref"] = m.Ref,
                    ["name"] = ShowNames ? m.Name : "Model " + (char)('A' + i % 26) + (i >= 26 ? (i / 26 + 1).ToString(CultureInfo.InvariantCulture) : ""),
                    ["id"] = ShowNames ? m.ModelId : null,
                    ["runs"] = m.Runs,
                });
            }

            all[skill] = array;
        }

        return all;
    }
}
