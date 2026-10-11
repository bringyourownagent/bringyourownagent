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

public sealed record NoticeModel(string Origin, string Host);

/// <summary>One language of a skill and the source the manager suggests for it.</summary>
public sealed record RouteLine(string? Language, string SourceName, string Where, bool Paid, bool Fits);

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
public sealed record ConsentModel(
    string Origin,
    string Host,
    bool Secure,
    string Summary,
    IReadOnlyList<ConsentRow> Rows,
    decimal Budget,
    decimal? BudgetBefore,
    bool AutoDecide,
    bool IsDifference)
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

    /// <summary>A request without a click was held back: the tray icon gets a quiet badge.</summary>
    void QuietRequest(string origin);

    /// <summary>A model started downloading on first use: show its progress.</summary>
    void DownloadStarted(ISpeechSource source);

    /// <summary>Open the wallet's window, at a page ("sites", "sources", "skills", "limits", "activity") or the last one.</summary>
    void OpenSettings(string? page = null);
}

/// <summary>A UI that answers nothing (tests, or a wallet without a desktop): every question is a no.</summary>
public sealed class NoUi : IWalletUi
{
    public Task<NoticeAnswer> ShowNoticeAsync(NoticeModel model, CancellationToken ct) => Task.FromResult(NoticeAnswer.Closed);
    public Task<ConsentAnswer> ShowConsentAsync(ConsentModel model, CancellationToken ct) => Task.FromResult(ConsentAnswer.Declined);
    public Task<PaidAnswer> AskPaidAsync(PaidModel model, CancellationToken ct) => Task.FromResult(PaidAnswer.No);
    public void ShowInfo(InfoModel model) { }
    public void QuietRequest(string origin) { }
    public void DownloadStarted(ISpeechSource source) { }
    public void OpenSettings(string? page = null) { }
}
