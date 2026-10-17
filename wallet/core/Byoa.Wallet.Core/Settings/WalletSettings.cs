using System.Text.Json.Serialization;

namespace Byoa.Wallet.Settings;

/// <summary>What the visitor decided for one site (one origin).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SiteStatus>))]
public enum SiteStatus
{
    /// <summary>Not allowed yet, or revoked.</summary>
    None,
    Allowed,
    Declined,
    Blocked,
}

/// <summary>Everything the wallet keeps on disk (settings.json in the data folder). No secrets here.</summary>
public sealed class WalletSettings
{
    public int Version { get; set; } = 1;
    public List<SiteRecord> Sites { get; set; } = [];
    public List<SourceSetting> Sources { get; set; } = [];
    public List<SkillChain> Chains { get; set; } = [];
    public List<AgentRecord> Agents { get; set; } = [];
    public LimitSettings Limits { get; set; } = new();

    /// <summary>Random per-wallet key used to make redacted site labels opaque to agents.</summary>
    public string? SiteActivityLabelKey { get; set; }

    public SiteRecord? Site(string origin) => Sites.FirstOrDefault(s => s.Origin == origin);

    public SourceSetting Source(string id)
    {
        var s = Sources.FirstOrDefault(x => x.Id == id);
        if (s is null)
        {
            s = new SourceSetting { Id = id };
            Sources.Add(s);
        }

        return s;
    }

    public SkillChain Chain(string skill)
    {
        return Chain(skill, null);
    }

    /// <summary>Gets the route for a skill and optional primary language.</summary>
    public SkillChain Chain(string skill, string? language)
    {
        var primary = string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();
        var c = Chains.FirstOrDefault(x => x.Skill == skill && x.Language == primary);
        if (c is null)
        {
            c = new SkillChain { Skill = skill, Language = primary };
            Chains.Add(c);
        }

        return c;
    }
}

public sealed class SiteRecord
{
    public string Origin { get; set; } = "";
    public SiteStatus Status { get; set; }

    /// <summary>"Next time, decide on my own within these limits".</summary>
    public bool AutoDecide { get; set; }

    /// <summary>The skills the visitor allowed.</summary>
    public List<string> Skills { get; set; } = [];

    /// <summary>The visitor's budget per session (spec money).</summary>
    public string Budget { get; set; } = "0.20 USD";

    /// <summary>The site's own expected cost per session when it was allowed; a higher one is "asking for more".</summary>
    public string? ExpectedCost { get; set; }

    public string? Summary { get; set; }
    public string? Privacy { get; set; }

    /// <summary>The languages the site declared for its skills, so the wallet can warm the right models.</summary>
    public List<string> Languages { get; set; } = [];

    /// <summary>"Always for this site": paid fallbacks may run without asking (still within the budget).</summary>
    public bool PaidAllowed { get; set; }

    /// <summary>The site's own daily money limit; null means the default from <see cref="LimitSettings"/>.</summary>
    public string? PerDay { get; set; }

    /// <summary>The site's own daily call limit; null means the default.</summary>
    public int? CallsPerDay { get; set; }

    /// <summary>Requests made without a click and held back (shown in Sites, cleared when seen).</summary>
    public int QuietRequests { get; set; }

    public DateTimeOffset? LastRequest { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>
    /// Paused: the site keeps what it was allowed, but nothing runs for it on its own any more: no grant without asking,
    /// no reconnecting by itself, and calls on its old grants are refused. Its next request, after a click, asks to resume.
    /// </summary>
    public bool Paused { get; set; }

    public DateTimeOffset? PausedAt { get; set; }

    /// <summary>"you" (paused in Sites) or "unused" (no use for longer than <see cref="LimitSettings.PauseUnusedAfterHours"/>).</summary>
    public string? PauseReason { get; set; }
}

/// <summary>
/// The visitor's switch for one source. The paid OpenRouter speech source describes itself in code and keeps only
/// <see cref="Id"/> and <see cref="Enabled"/> here; sources the visitor (or his agent) adds also carry what they are:
/// <see cref="Kind"/>, <see cref="Model"/> and the rest. The wallet holds no model of its own: every added source is
/// a program on this computer, or a server, that runs one.
/// </summary>
public sealed class SourceSetting
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>One of <c>SourceKinds</c> for an added source; null for a built-in one.</summary>
    public string? Kind { get; set; }

    /// <summary>The name people see; null means one made from the kind and the model.</summary>
    public string? Label { get; set; }

    /// <summary>The runtime's base URL; null means the kind's default.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The runtime's or the API's model id.</summary>
    public string? Model { get; set; }

    /// <summary>The skills it serves: ["text.generate"] or ["audio.transcribe"] (one added source serves one skill).</summary>
    public List<string> Skills { get; set; } = [];

    /// <summary>Primary language tags it is used for; empty means any.</summary>
    public List<string> Languages { get; set; } = [];

    /// <summary>The name of its API key in the secret store (never the key itself); null when it needs none.</summary>
    public string? SecretRef { get; set; }

    /// <summary>True after the visitor explicitly accepted a non-loopback endpoint for this source.</summary>
    public bool EndpointConfirmed { get; set; }

    /// <summary>Marks a display label supplied by an agent, rather than typed by the visitor.</summary>
    public bool LabelFromAgent { get; set; }

    /// <summary>True when an agent created this setting; agent-originated probe results stay redacted.</summary>
    public bool AddedByAgent { get; set; }

    /// <summary>Addresses observed during visitor confirmation of a name-based agent endpoint.</summary>
    public List<string> ConfirmedAddresses { get; set; } = [];

    /// <summary>The address class observed at confirmation: public, private, or unknown.</summary>
    public string? ConfirmedAddressClass { get; set; }
}

/// <summary>An ordered chain of sources for one skill: the first that fits answers, the next ones are fallbacks.</summary>
public sealed class SkillChain
{
    public string Skill { get; set; } = "";
    /// <summary>Primary language for a language-specific route; null is the default route.</summary>
    public string? Language { get; set; }
    public List<ChainStop> Stops { get; set; } = [];
}

public sealed class ChainStop
{
    public string SourceId { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>For a paid source: ask the visitor before using it.</summary>
    public bool AskBeforePaid { get; set; } = true;
}

/// <summary>Limits that apply on top of every grant.</summary>
public sealed class LimitSettings
{
    /// <summary>The budget per session the consent window proposes when the site states no expected cost.</summary>
    public string DefaultSessionBudget { get; set; } = "0.20 USD";

    /// <summary>Money per site per day (default for sites without their own).</summary>
    public string SitePerDay { get; set; } = "0.20 USD";

    /// <summary>Calls per site per day, local or paid (default for sites without their own).</summary>
    public int SiteCallsPerDay { get; set; } = 5000;

    /// <summary>Money for the whole wallet per day, all sites together.</summary>
    public string WalletPerDay { get; set; } = "1.00 USD";

    /// <summary>An allowed site that has not used the wallet for this many hours is paused; 0 = never.</summary>
    public int PauseUnusedAfterHours { get; set; } = 24;
}

/// <summary>The persisted identity and access granted to one local AI agent using the MCP bridge.</summary>
public sealed class AgentRecord
{
    /// <summary>A stable hash of the wallet-observed parent image path, or the bridge image when the parent is unknown.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public DateTimeOffset? LastUsed { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public bool FullAccess { get; set; }

    /// <summary>"allowed" or "blocked". A missing record is asked about on the next connection.</summary>
    public string Status { get; set; } = "allowed";
}
