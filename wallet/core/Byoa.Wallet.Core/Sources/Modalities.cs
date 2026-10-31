namespace Byoa.Wallet.Sources;

/// <summary>The modality words of the property vocabulary (PROPOSAL-v2 3.2): what goes into a source and what comes out of it.</summary>
public static class Modalities
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Audio = "audio";

    /// <summary>The words the wallet knows. Anything else in a manifest is a condition the wallet cannot check, and is not one.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { Text, Image, Audio };

    /// <summary>
    /// The primary-role rule for <c>text.generate</c> (PROPOSAL-v2 3.3): a source whose output is unknown is offered, one that writes text
    /// and does not draw pictures is offered, and one that draws pictures is not. Only the visitor's own "also use as a text model" tick
    /// for that source lifts the last clause, and the fit engine applies the tick; a site's key never does.
    /// </summary>
    public static bool WritesText(IReadOnlyList<string>? outputs) =>
        outputs is null || outputs.Contains(Text) && !outputs.Contains(Image);
}
