using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Everywhere;

/// <summary>
/// How a call went. <see cref="Text"/> is what the model answered (empty when it did not); <see cref="Error"/> says in plain words why there is none.
/// The text lives in memory only: neither the activity log nor any file keeps it.
/// </summary>
public sealed record OwnResult(bool Ok, string Text, string? Error)
{
    public static OwnResult Success(string text) => new(true, text, null);

    public static OwnResult Failure(string error) => new(false, "", error);
}

/// <summary>
/// The wallet's calls for itself: no website, no grant. They run by the visitor's own routes, within his budgets, and each one is written to the
/// activity log under the name "Everywhere" (the engine implements this).
/// </summary>
public interface IOwnCalls
{
    /// <param name="samples">16 kHz mono samples in -1..1.</param>
    /// <param name="language">A primary language tag, or null to let the speech model find it.</param>
    /// <param name="ct">Cancels the call.</param>
    Task<OwnResult> TranscribeAsync(float[] samples, string? language, CancellationToken ct);

    /// <summary>Asks the first text model of the visitor's route that answers. Never one of his agents.</summary>
    Task<OwnResult> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}
