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
    public LimitSettings Limits { get; set; } = new();

    /// <summary>Folders searched for local models (GigaAM folders, ggml Whisper files).</summary>
    public List<string> ModelFolders { get; set; } = [];

    /// <summary>The ggml Whisper model the wallet downloads and uses.</summary>
    public string WhisperModel { get; set; } = "ggml-large-v3-q5_0.bin";

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
        var c = Chains.FirstOrDefault(x => x.Skill == skill);
        if (c is null)
        {
            c = new SkillChain { Skill = skill };
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
}

/// <summary>The visitor's switch for one source.</summary>
public sealed class SourceSetting
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>An ordered chain of sources for one skill: the first that fits answers, the next ones are fallbacks.</summary>
public sealed class SkillChain
{
    public string Skill { get; set; } = "";
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
}
