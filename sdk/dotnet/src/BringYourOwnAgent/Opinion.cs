using System;
using System.Text.Json;

namespace BringYourOwnAgent;

/// <summary>
/// A purpose judge: decides whether one call fits the requester's declared purpose.
/// Returns an <see cref="Opinion"/>, or null for no opinion. If it throws, the gate asks the visitor.
/// </summary>
/// <param name="manifest">The requester's manifest, as passed to the gate.</param>
/// <param name="grant">The visitor's grant, as passed to the gate.</param>
/// <param name="call">The call being checked, as passed to the gate.</param>
/// <remarks>
/// The judge treats the content as data, never as instructions, because the content comes from the requester.
/// A judge that calls a model is usually asynchronous: check the call without a judge, await the judge
/// yourself, then use <see cref="Gate.ApplyOpinion(CheckResult, Opinion?)"/>.
/// </remarks>
public delegate Opinion? PurposeJudge(JsonElement manifest, JsonElement grant, JsonElement call);

/// <summary>What a purpose judge thinks of one call.</summary>
public sealed class Opinion
{
    /// <summary>Creates an opinion.</summary>
    /// <param name="decision">The decision the judge asks for. An opinion can only make the gate's decision stricter.</param>
    /// <param name="code">The reason code to report; null means <c>purpose-doubt</c>. Must not be empty.</param>
    /// <param name="message">An explanation for people; null or empty means a default message.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="decision"/> is not one of the four decisions.</exception>
    /// <exception cref="ArgumentException"><paramref name="code"/> is the empty string.</exception>
    public Opinion(Decision decision, string? code = null, string? message = null)
    {
        if (!DecisionExtensions.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "not a BYOA decision");
        }

        if (code is not null && code.Length == 0)
        {
            throw new ArgumentException("the code must be null or a non-empty string", nameof(code));
        }

        Decision = decision;
        Code = code;
        Message = message;
    }

    /// <summary>The decision the judge asks for.</summary>
    public Decision Decision { get; }

    /// <summary>The reason code; null means <c>purpose-doubt</c>.</summary>
    public string? Code { get; }

    /// <summary>An explanation for people, or null.</summary>
    public string? Message { get; }
}
