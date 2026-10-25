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

    /// <summary>The guard that looks at every request a site makes to the visitor's own agent.</summary>
    public GuardSettings Guard { get; set; } = new();

    /// <summary>
    /// When a site connects, each model it will use is made to do its simplest job once for real (speech: transcribe 3 seconds of
    /// audio; text: write one short answer), and the connection waits for that, so the site's first real request does not meet a
    /// model that is still starting. On by default; an older settings file without the field gets it on too.
    /// </summary>
    public bool WarmUpOnConnect { get; set; } = true;

    /// <summary>
    /// True when the visitor wants to start his models himself: the wallet makes no model do a warm-up job, and sends nothing to a local model that is
    /// not in memory (a route skips it, so no runtime starts it for a request). Only a runtime that says what is in memory can be held to it: the Llama
    /// app, LM Studio, Ollama. Off by default: the wallet then lets a runtime start the model a site's request needs.
    /// </summary>
    public bool NeverStartModels { get; set; }

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

    // The visitor's own text limits for this site; null means the default from LimitSettings (see TextLimits).
    public int? TextInputChars { get; set; }
    public int? TextAnswerTokens { get; set; }
    public int? TextCallSeconds { get; set; }
    public int? ParallelCalls { get; set; }
    public int? CallsPerMinute { get; set; }

    /// <summary>What the site asked for above those limits the last time the visitor saw its request, so the same ask is not put to him again.</summary>
    public SiteAsk? Asked { get; set; }

    /// <summary>The models the visitor gives this site, per skill, in his order (ids of added sources).</summary>
    public List<SiteModels> Models { get; set; } = [];

    /// <summary>The site sees the names of its models (only when it asked for them). On by default; the visitor can hide them.</summary>
    public bool ShowModelNames { get; set; } = true;

    /// <summary>
    /// What the site said it uses the visitor's agent for (its manifest's <c>purpose.summary</c>) the last time the visitor, or the guard, accepted it.
    /// A site whose stated goal is no longer this one is asked about again; a call that finds them different is refused.
    /// </summary>
    public string? AgentPurposeOk { get; set; }

    public SiteModels? ModelsFor(string skill) => Models.FirstOrDefault(m => m.Skill == skill);
}

/// <summary>The sources the visitor gives one site for one skill, in his order.</summary>
public sealed class SiteModels
{
    public string Skill { get; set; } = "";
    public List<string> SourceIds { get; set; } = [];
}

/// <summary>The limits a site asked for (its manifest's declared limits), as the visitor last saw them.</summary>
public sealed class SiteAsk
{
    public int? InputChars { get; set; }
    public int? AnswerTokens { get; set; }
    public int? CallSeconds { get; set; }
    public int? ParallelCalls { get; set; }
    public int? CallsPerMinute { get; set; }
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

    /// <summary>The service behind an API-key source (<c>ApiServices</c>: openai, anthropic...); null for a local program and for OpenRouter.</summary>
    public string? Service { get; set; }

    /// <summary>The model's name as everyone knows it ("google/gemma-3-1b-it"), so a site that asks for it by name finds it; null means the wallet guesses it.</summary>
    public string? ModelId { get; set; }

    /// <summary>In the visitor's default set: the wallet offers this model first when a site asks for several and names none he has.</summary>
    public bool Offer { get; set; } = true;

    /// <summary>
    /// False when the visitor does not let this model think before it answers: a model that reasons first (Qwen3.5, DeepSeek-R1 and the like) is
    /// asked to answer straight away, whatever a site wants. True (the default) leaves it to the model and to the site's own request.
    /// </summary>
    public bool Thinking { get; set; } = true;

    /// <summary>The most tokens a thinking model of the Llama app or another llama.cpp server may spend on reasoning before it answers; 0 is no limit.
    /// Without a limit a small model can reason in circles for minutes. A source that cannot limit reasoning ignores it.</summary>
    public int ThinkingBudget { get; set; } = DefaultThinkingBudget;

    public const int DefaultThinkingBudget = 2000;
    public const int MaxThinkingBudget = 20000;

    /// <summary>Addresses observed during visitor confirmation of a name-based agent endpoint.</summary>
    public List<string> ConfirmedAddresses { get; set; } = [];

    /// <summary>The address class observed at confirmation: public, private, or unknown.</summary>
    public string? ConfirmedAddressClass { get; set; }

    /// <summary>For an agent source: the program that runs it (a launcher such as ccodeglm.cmd, or claude.exe). Only the visitor sets it.</summary>
    public string? AgentCommand { get; set; }

    /// <summary>For an agent source: how to talk to the program, one of <c>AgentFlavors</c>.</summary>
    public string? AgentFlavor { get; set; }

    /// <summary>For an agent source: the model the program is asked to use (<c>--model opus</c>), or null for the program's own choice. Only the visitor sets it.</summary>
    public string? AgentModel { get; set; }
}

/// <summary>
/// The guard: before the visitor's agent answers a site's request, a small fast model (Jev, behind the visitor's OpenRouter key) answers a few
/// yes-or-no questions about it. A request that looks harmful, that tries to give the agent orders, or that is not what the site said it is
/// for, waits for the visitor.
/// </summary>
public sealed class GuardSettings
{
    public const string DefaultDailyLimit = "2.00 USD";
    public const string Strict = "strict";
    public const string Normal = "normal";
    public const string Relaxed = "relaxed";

    /// <summary>The most the guard may spend a day (it is one cent in a hundred requests; the limit is for a runaway).</summary>
    public string DailyLimit { get; set; } = DefaultDailyLimit;

    /// <summary>
    /// True (the default): when the guard cannot be asked (no key, no network, the daily limit reached), no request reaches the agent. False: the
    /// request goes through without a look. A site's goal that could not be checked is never recorded as accepted either way.
    /// </summary>
    public bool StopWhenUnavailable { get; set; } = true;

    /// <summary>"strict", "normal" or "relaxed": how soon a request is held for the visitor.</summary>
    public string Strictness { get; set; } = Normal;

    /// <summary>The visitor's own wording of each question; null or empty keeps the wallet's.</summary>
    public string? OnPurposeText { get; set; }
    public string? HarmText { get; set; }
    public string? RedirectText { get; set; }
    public string? PurposeRiskText { get; set; }
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

    // text.generate, for every site (a site may have its own, and may ask for more in its manifest; see TextLimits).
    public int TextInputChars { get; set; } = TextLimits.DefaultInputChars;
    public int TextAnswerTokens { get; set; } = TextLimits.DefaultAnswerTokens;
    public int TextCallSeconds { get; set; } = TextLimits.DefaultCallSeconds;
    public int ParallelCalls { get; set; } = TextLimits.DefaultParallelCalls;
    public int CallsPerMinute { get; set; } = TextLimits.DefaultCallsPerMinute;
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
