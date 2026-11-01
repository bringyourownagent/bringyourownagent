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
    /// The offering rule for <c>text.generate</c> (PROPOSAL-v3 1.1): a source whose output is unknown is offered, and so is one whose output
    /// includes text, whether it draws pictures as well or not. A model that only draws is not.
    /// </summary>
    public static bool WritesText(IReadOnlyList<string>? outputs) => outputs is null || outputs.Contains(Text);

    /// <summary>The offering rule for <c>image.generate</c> (PROPOSAL-v3 1.1): the output is known to include pictures. An unknown output is not enough.</summary>
    public static bool DrawsPictures(IReadOnlyList<string>? outputs) => outputs?.Contains(Image) == true;
}
