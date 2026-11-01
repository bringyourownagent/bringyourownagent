using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace BringYourOwnAgent.Relay;

/// <summary>
/// The pictures of one <c>image.generate</c> call as the wallet sends them (CONTRACT-DIRECT.md §4, wallet 0.37): every picture in
/// <c>image</c> frames of at most <see cref="MaxPartChars"/> base64 characters, then the <c>result</c> that says what each picture is.
/// Joined in <c>seq</c> order and checked against the result: the size, the SHA-256, the type the bytes really are, at most
/// <see cref="MaxPictures"/> pictures and <see cref="MaxBytes"/> bytes together. One bad part spoils the whole answer.
/// </summary>
public sealed class PictureParts
{
    /// <summary>The most pictures one call may ask for (CONTRACT.md §3.7).</summary>
    public const int MaxPictures = 4;

    /// <summary>The longest <c>data</c> of one part, in base64 characters.</summary>
    public const int MaxPartChars = 524_288;

    /// <summary>The most bytes the pictures of one answer may have together.</summary>
    public const int MaxBytes = 8_000_000;

    /// <summary>The most parts one picture may have: <see cref="MaxBytes"/> in parts of <see cref="MaxPartChars"/>, with room to spare.</summary>
    public const int MaxParts = 32;

    const long MaxChars = (MaxBytes + 2L) / 3 * 4;

    readonly Dictionary<int, (string Mime, string?[] Parts)> _pictures = new();
    long _chars;
    bool _broken;

    /// <summary>One part; a part out of shape, a repeat, a part that disagrees with the picture's earlier ones, or one past the budget breaks the answer.</summary>
    public void Add(int? index, int? seq, int? of, string? mime, string? data)
    {
        lock (_pictures)
        {
            if (_broken) return;
            if (index is not (>= 0 and < MaxPictures) || of is not (>= 1 and <= MaxParts) || seq is not { } s || s < 0 || s >= of
                || !Types.Contains(mime) || string.IsNullOrEmpty(data) || data.Length > MaxPartChars || (_chars += data.Length) > MaxChars)
            {
                _broken = true;
                return;
            }
            if (!_pictures.TryGetValue(index.Value, out var picture)) _pictures[index.Value] = picture = (mime!, new string?[of.Value]);
            if (picture.Mime != mime || picture.Parts.Length != of || picture.Parts[s] is not null)
            {
                _broken = true;
                return;
            }
            picture.Parts[s] = data;
        }
    }

    /// <summary>The pictures <paramref name="described"/> (the result's <c>output.images</c>, in order), joined from their parts and checked;
    /// null when a picture is missing a part, has a part too many, or its bytes do not match what the result says.</summary>
    public IReadOnlyList<WalletPicture>? Join(IReadOnlyList<PictureInfo> described)
    {
        lock (_pictures)
        {
            if (_broken || described.Count is 0 or > MaxPictures || described.Count != _pictures.Count) return null;
            var result = new List<WalletPicture>();
            long total = 0;
            for (var i = 0; i < described.Count; i++)
            {
                var info = described[i];
                if (!_pictures.TryGetValue(i, out var picture) || picture.Parts.Any(p => p is null) || info.Mime != picture.Mime) return null;
                var data = string.Concat(picture.Parts);
                byte[] bytes;
                try { bytes = Convert.FromBase64String(data); }
                catch (FormatException) { return null; }
                total += bytes.Length;
                if (total > MaxBytes || info.Size != bytes.Length || Sniff(bytes) != picture.Mime) return null;
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(info.Sha256, sha, StringComparison.OrdinalIgnoreCase)) return null;
                result.Add(new WalletPicture(picture.Mime, bytes.Length, sha, data, info.Reduced));
            }
            return result;
        }
    }

    /// <summary>The picture types a wallet may send.</summary>
    public static readonly IReadOnlySet<string?> Types = new HashSet<string?>(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/webp", "image/gif" };

    /// <summary>The type the bytes are, by their first bytes (never by a field); null for anything else.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38 && (b[4] == 0x37 || b[4] == 0x39) && b[5] == 0x61) return "image/gif";
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        return null;
    }
}
