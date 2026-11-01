namespace Byoa.Wallet.Sources;

// The contract for the skill image.generate (PROPOSAL-v3 4.4): a source that can draw pictures, and the helpers the engine uses on what it
// draws. Interfaces and small records only; the OpenRouter key source implements it.

/// <summary>One picture the site sent for a model to look at, as bytes.</summary>
/// <param name="Mime">What the wallet detected from the bytes (never what the site said).</param>
public sealed record Picture(string Mime, byte[] Data);

/// <summary>One image.generate call after the wallet has checked it.</summary>
/// <param name="Prompt">The text to draw, at most the site's <c>maxTextChars</c>.</param>
/// <param name="References">Pictures for the model to look at; only for a source whose input is known to include pictures.</param>
/// <param name="Count">How many pictures the site wants, 1 to 4.</param>
/// <param name="Aspect">One of <see cref="Pictures.Aspects"/>, or null for the model's own.</param>
public sealed record ImageRequest(string Prompt, IReadOnlyList<Picture> References, int Count, string? Aspect);

/// <summary>What one request to a picture model gave.</summary>
/// <param name="Pictures">The bytes of each picture as the provider sent them; the wallet detects their type and limits them.</param>
/// <param name="Cost">What the provider says the request cost, or null when it said nothing.</param>
public sealed record ImageAnswer(IReadOnlyList<byte[]> Pictures, decimal? Cost, string? Detail = null);

/// <summary>A source that can draw pictures: a model behind the visitor's OpenRouter key. The same object also answers text.</summary>
public interface IImageSource : ITextSource
{
    /// <summary>The estimated cost of one request (one picture), before it runs: 0 when the public list says the model is free, a round figure otherwise.</summary>
    decimal EstimateImageCost(ImageRequest request);

    /// <summary>One request to the model. Throws <see cref="TextSourceException"/> when it answered nothing usable.</summary>
    Task<ImageAnswer> GenerateImageAsync(ImageRequest request, CancellationToken ct);
}

/// <summary>
/// Makes a picture smaller (ADDENDUM-0370-1). The desktop app supplies one; the core has no picture library. With none set, the engine sends
/// a picture only when it needs no change that it can see without one.
/// </summary>
public interface IPictureShrinker
{
    /// <summary>
    /// The picture within the limits: itself when it already is, or smaller, in the same type (a PNG stays a PNG, a JPEG and a WebP are encoded again).
    /// Null when it cannot be made to fit: it is over a limit and may not be changed (a GIF), or it would have to be smaller than 256 pixels.
    /// </summary>
    /// <param name="maxSide">The longest side the site needs, in pixels; null keeps the picture's own.</param>
    /// <param name="maxBytes">The most bytes the picture may have.</param>
    ShrunkPicture? Shrink(byte[] picture, string mime, int? maxSide, int maxBytes);
}

/// <param name="Reduced">True when the bytes are not the picture the model drew.</param>
public sealed record ShrunkPicture(byte[] Bytes, bool Reduced);

/// <summary>What the wallet knows of pictures without a picture library: the types it lets through, and the limits of a call.</summary>
public static class Pictures
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string WebP = "image/webp";
    public const string Gif = "image/gif";

    /// <summary>The aspect ratios a site may ask for.</summary>
    public static readonly IReadOnlySet<string> Aspects = new HashSet<string>(StringComparer.Ordinal) { "1:1", "4:3", "3:4", "16:9", "9:16" };

    public const int MaxCount = 4;
    public const int MaxReferences = 4;
    public const int MaxReferenceBytes = 1_000_000;
    public const int MaxReferencesBytes = 3_000_000;
    public const int MaxAnswerBytes = 8_000_000;
    public const int PicturesPerMinute = 20;
    public const int MinSide = 64;
    public const int MaxSide = 4096;
    public const int MinBytes = 100_000;
    public const int MaxBytes = 3_000_000;
    public const int DefaultBytes = 1_000_000;

    /// <summary>The picture type from the first bytes (PNG, JPEG, WebP, GIF), or null for anything else. A provider's own word for it is never used.</summary>
    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return Png;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return Jpeg;
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return WebP;
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return Gif;
        return null;
    }

    /// <summary>The bytes of a <c>data:image/...;base64,...</c> URL, or null for anything else (a link to fetch is never followed).</summary>
    public static byte[]? FromDataUrl(string? url)
    {
        const string prefix = "data:";
        if (url is null || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || comma > 100 || !url[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
        var payload = url.AsSpan(comma + 1);
        var bytes = new byte[payload.Length / 4 * 3 + 3];
        return Convert.TryFromBase64Chars(payload, bytes, out var written) ? bytes.AsSpan(0, written).ToArray() : null;
    }
}
