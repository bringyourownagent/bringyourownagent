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

/// <param name="FromLink">The request came from a direct link: nothing is stored from its notice, which offers no block.</param>
public sealed record NoticeModel(string Origin, string Host, bool FromLink = false);

/// <summary>One language of a skill and the source the manager suggests for it.</summary>
public sealed record RouteLine(string? Language, string SourceName, string Where, bool Paid, bool Fits, string? Detail = null);

/// <summary>One row of the consent window: a skill, the sources that will answer it, and the reason.</summary>
public sealed record ConsentRow(
    string Skill,
    string SkillName,
    bool Required,
    bool IsNew,
    bool CanServe,
    IReadOnlyList<RouteLine> Routes,
    IReadOnlyList<string> Languages,
    string Why);

/// <summary>Everything the short consent window shows.</summary>
/// <param name="IsDifference">The site already has a grant and asks for more: only the new parts are shown.</param>
/// <param name="BudgetBefore">With <paramref name="IsDifference"/>, the budget the visitor allowed before.</param>
/// <param name="PausedNote">The site was allowed before and is paused ("Paused after a day without use"): Allow resumes it.</param>
/// <param name="FromLink">The request came from a direct link, so the browser has not vouched for the origin yet.</param>
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
    bool FromLink = false)
{
    /// <summary>A required skill no source can serve: Allow is not offered.</summary>
    public bool MissingRequired => Rows.Any(r => r.Required && !r.CanServe);
}

/// <summary>The visitor's answer in the consent window.</summary>
/// <param name="LeftOff">Optional skills the visitor unticked.</param>
public sealed record ConsentAnswer(bool Allow, decimal Budget, bool AutoDecide, IReadOnlyList<string> LeftOff)
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
