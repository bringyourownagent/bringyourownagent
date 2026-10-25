using Byoa.Wallet.Guard;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>The visitor's answer to the short notice ("site wants to use your AI").</summary>
public enum NoticeAnswer
{
    Show,
    Decline,
    Block,

    /// <summary>Closed with ×, Esc, or no answer in time.</summary>
    Closed,
}

/// <summary>The visitor's answer to "use a paid fallback?".</summary>
public enum PaidAnswer
{
    Once,
    Always,
    No,
}

/// <summary>The visitor's answer when the guard holds a request, or a site's goal, for him.</summary>
public enum GuardAnswer
{
    /// <summary>Let this one through (a goal: accept it).</summary>
    Allow,

    /// <summary>Refuse this one.</summary>
    Refuse,

    /// <summary>Refuse it and take the agent away from this site.</summary>
    TakeAway,
}

/// <summary>
/// What the guard saw and why the visitor is asked: a request that tripped a statement (<paramref name="ForPurpose"/> false), or the goal a site
/// states for his agent (true), or the guard could not be asked and the visitor chose to be told (<paramref name="Unavailable"/>).
/// </summary>
/// <param name="Purpose">What the site said it uses the agent for.</param>
/// <param name="Request">The site's request as it would go to the agent (shortened); null for a goal.</param>
/// <param name="Agent">The visitor's name for the agent.</param>
public sealed record GuardPrompt(string Origin, string Host, bool ForPurpose, string Purpose, string? Request, IReadOnlyList<GuardFinding> Findings, string Agent);

/// <param name="FromLink">The request came from a direct link: nothing is stored from its notice, which offers no block.</param>
public sealed record NoticeModel(string Origin, string Host, bool FromLink = false);

/// <summary>One language of a skill and the source the manager suggests for it.</summary>
public sealed record RouteLine(string? Language, string SourceName, string Where, bool Paid, bool Fits, string? Detail = null);

/// <summary>
/// The languages of a skill that go to the same source, as one line: a site that lists four languages, all read by one
/// local model, shows that model once, not four times. The groups keep the order in which their first language came.
/// </summary>
public sealed record RouteGroup(RouteLine Route, IReadOnlyList<string> Languages)
{
    public static IReadOnlyList<RouteGroup> Of(IEnumerable<RouteLine> routes)
    {
        var groups = new List<(RouteLine Route, List<string> Languages)>();
        foreach (var route in routes)
        {
            var at = groups.FindIndex(g => g.Route.SourceName == route.SourceName && g.Route.Where == route.Where
                && g.Route.Paid == route.Paid && g.Route.Fits == route.Fits && g.Route.Detail == route.Detail);
            if (at < 0)
            {
                groups.Add((route, []));
                at = groups.Count - 1;
            }

            if (route.Language is { } language && !groups[at].Languages.Contains(language)) groups[at].Languages.Add(language);
        }

        return groups.Select(g => new RouteGroup(g.Route, g.Languages)).ToList();
    }
}

/// <summary>One row of the consent window: a skill, the sources that will answer it, and the reason.</summary>
/// <param name="Models">For a skill whose site asked for several models: the models to tick, instead of the route lines.</param>
public sealed record ConsentRow(
    string Skill,
    string SkillName,
    bool Required,
    bool IsNew,
    bool CanServe,
    IReadOnlyList<RouteLine> Routes,
    IReadOnlyList<string> Languages,
    string Why,
    ModelSection? Models = null);

/// <summary>What the site asked for above the visitor's own limits, and what those limits are now.</summary>
/// <param name="Wanted">Only the values above the visitor's (each at most the wallet's ceiling).</param>
public sealed record LimitsAsk(RequestedLimits Wanted, TextLimitSet Current);

/// <summary>Everything the short consent window shows.</summary>
/// <param name="IsDifference">The site already has a grant and asks for more: only the new parts are shown.</param>
/// <param name="BudgetBefore">With <paramref name="IsDifference"/>, the budget the visitor allowed before.</param>
/// <param name="PausedNote">The site was allowed before and is paused ("Paused after a day without use"): Allow resumes it.</param>
/// <param name="FromLink">The request came from a direct link, so the browser has not vouched for the origin yet.</param>
/// <param name="CheckCode">A link's connect check code (CONTRACT-DIRECT.md section 3a), shown for the visitor to compare with the site's page; null when there is none.</param>
/// <param name="CheckRequired">The site says it shows the code: Allow needs the visitor's tick that the two are the same.</param>
public sealed record ConsentModel(
    string Origin,
    string Host,
    bool Secure,
    string Summary,
    IReadOnlyList<ConsentRow> Rows,
    decimal Budget,
    decimal? BudgetBefore,
    bool AutoDecide,
    bool IsDifference,
    string? PausedNote = null,
    bool FromLink = false,
    LimitsAsk? Limits = null,
    string? CheckCode = null,
    bool CheckRequired = false)
{
    /// <summary>A required skill no source can serve: Allow is not offered.</summary>
    public bool MissingRequired => Rows.Any(r => r.Required && !r.CanServe);
}

/// <summary>The visitor's answer in the consent window.</summary>
/// <param name="LeftOff">Optional skills the visitor unticked.</param>
/// <param name="Models">The models he ticked for each skill that asked for several (the keys of <see cref="ModelChoice"/>), in order; null takes the wallet's proposal.</param>
/// <param name="ShowNames">The site may see the names of its models.</param>
/// <param name="UseSiteLimits">Accept the limits the site asked for above his own.</param>
/// <param name="CheckConfirmed">The visitor ticked that the check code is the same on the site's page.</param>
public sealed record ConsentAnswer(
    bool Allow, decimal Budget, bool AutoDecide, IReadOnlyList<string> LeftOff,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Models = null, bool ShowNames = true, bool UseSiteLimits = true, bool CheckConfirmed = false)
{
    public static readonly ConsentAnswer Declined = new(false, 0, false, []);
}

/// <summary>"Your local models could not answer: use a paid source?"</summary>
public sealed record PaidModel(string Origin, string Host, string SourceName, decimal Estimate, string Why);

/// <summary>"Preparing your model…": the routed models are loading before the page gets its grant.</summary>
public sealed record PreparingModel(string Origin, string Host, IReadOnlyList<string> Sources);

/// <summary>A short message that needs no answer, with an optional button that opens a settings page.</summary>
public sealed record InfoModel(string Title, string Text, string? ActionText = null, string? OpenPage = null);

/// <summary>
/// What the engine needs from the desktop UI. Every method may be called from any thread; the UI marshals to its
/// own thread. Cancellation means "no answer in time": the window closes and the engine treats it as a no.
/// </summary>
public interface IWalletUi
{
    Task<NoticeAnswer> ShowNoticeAsync(NoticeModel model, CancellationToken ct);

    Task<ConsentAnswer> ShowConsentAsync(ConsentModel model, CancellationToken ct);

    Task<PaidAnswer> AskPaidAsync(PaidModel model, CancellationToken ct);

    void ShowInfo(InfoModel model);

    /// <summary>The guard held a request or a site's goal for the visitor. No answer in time is a refusal.</summary>
    Task<GuardAnswer> AskGuardAsync(GuardPrompt model, CancellationToken ct) => Task.FromResult(GuardAnswer.Refuse);

    /// <summary>Shows "Preparing your model…" until the returned handle is disposed.</summary>
    IDisposable ShowPreparing(PreparingModel model);

    /// <summary>A request without a click was held back: the tray icon gets a quiet badge.</summary>
    void QuietRequest(string origin);

    /// <summary>Open the wallet's window, at a page ("sites", "sources", "skills", "limits", "activity") or the last one.</summary>
    void OpenSettings(string? page = null);
}

/// <summary>A handle that does nothing when disposed.</summary>
public sealed class Nothing : IDisposable
{
    public static readonly Nothing Instance = new();

    public void Dispose()
    {
    }
}

/// <summary>A UI that answers nothing (tests, or a wallet without a desktop): every question is a no.</summary>
public sealed class NoUi : IWalletUi
{
    public Task<NoticeAnswer> ShowNoticeAsync(NoticeModel model, CancellationToken ct) => Task.FromResult(NoticeAnswer.Closed);
    public Task<ConsentAnswer> ShowConsentAsync(ConsentModel model, CancellationToken ct) => Task.FromResult(ConsentAnswer.Declined);
    public Task<PaidAnswer> AskPaidAsync(PaidModel model, CancellationToken ct) => Task.FromResult(PaidAnswer.No);
    public void ShowInfo(InfoModel model) { }
    public IDisposable ShowPreparing(PreparingModel model) => Nothing.Instance;
    public void QuietRequest(string origin) { }
    public void OpenSettings(string? page = null) { }
}
