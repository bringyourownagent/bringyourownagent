using Byoa.Wallet.Sources;
using SkiaSharp;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Makes a picture smaller with the picture library the application already has (ADDENDUM-0370-1): scaled to the site's longest side when it is
/// bigger, then by a fifth at a time while it is over the byte limit, in its own type (a PNG stays a PNG and keeps its transparency; a JPEG and a WebP are
/// encoded again at quality 90). A GIF is never changed, since it may move. A picture that would have to shrink below 256 pixels is dropped.
/// </summary>
sealed class SkiaPictureShrinker : IPictureShrinker
{
    /// <summary>The longest side never goes below this to save bytes.</summary>
    public const int FewestPixels = 256;

    /// <summary>What each step keeps of the side while the picture is over the byte limit.</summary>
    const double Step = 0.8;

    const int Quality = 90;

    public ShrunkPicture? Shrink(byte[] picture, string mime, int? maxSide, int maxBytes)
    {
        using var data = SKData.CreateCopy(picture);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("not a picture the library can read");
        var longest = Math.Max(codec.Info.Width, codec.Info.Height);
        var overSide = maxSide is { } side && longest > side;
        if (!overSide && picture.Length <= maxBytes) return new ShrunkPicture(picture, false);
        if (mime == Pictures.Gif) return null;                       // over a limit, and never changed

        using var bitmap = SKBitmap.Decode(codec, codec.Info) ?? throw new InvalidDataException("the picture could not be decoded");
        var scale = overSide ? (double)maxSide!.Value / longest : 1.0;
        var changed = overSide;
        while (true)
        {
            if (changed)
            {
                var bytes = Encode(bitmap, mime, scale);
                if (bytes is not null && bytes.Length <= maxBytes) return new ShrunkPicture(bytes, true);
            }

            scale *= Step;
            changed = true;
            if (Math.Round(longest * scale) < FewestPixels) return null;
        }
    }

    static byte[]? Encode(SKBitmap bitmap, string mime, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var resized = scale >= 1.0 ? bitmap.Copy() : bitmap.Resize(new SKImageInfo(width, height, bitmap.ColorType, bitmap.AlphaType), SKFilterQuality.High);
        if (resized is null) return null;
        using var image = SKImage.FromBitmap(resized);
        var format = mime switch
        {
            Pictures.Png => SKEncodedImageFormat.Png,
            Pictures.Jpeg => SKEncodedImageFormat.Jpeg,
            Pictures.WebP => SKEncodedImageFormat.Webp,
            _ => throw new InvalidDataException("a picture type the wallet does not change"),
        };
        using var encoded = image.Encode(format, mime == Pictures.Png ? 100 : Quality);
        return encoded?.ToArray();
    }
}
