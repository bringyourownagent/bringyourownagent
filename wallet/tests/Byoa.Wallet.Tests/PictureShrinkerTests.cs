using Byoa.Wallet.Desktop;
using Byoa.Wallet.Sources;
using SkiaSharp;

namespace Byoa.Wallet.Tests;

/// <summary>The real shrinker (ADDENDUM-0370-1), on pictures made here: a bigger picture is scaled down in its own type with its proportions, a picture within the limits is left alone, a GIF is never changed.</summary>
public class PictureShrinkerTests
{
    static readonly SkiaPictureShrinker Shrinker = new();

    /// <summary>A picture of noise, which does not compress: a big file from a modest size.</summary>
    static SKBitmap Noise(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var random = new Random(3);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 100)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    static (int Width, int Height) Size(byte[] picture)
    {
        using var data = SKData.CreateCopy(picture);
        using var codec = SKCodec.Create(data)!;
        return (codec.Info.Width, codec.Info.Height);
    }

    [Fact]
    public void ABigPngIsScaledDownInItsOwnTypeWithItsProportionsUntilItIsUnderTheLimit()
    {
        using var bitmap = Noise(1200, 800);
        var png = Encode(bitmap, SKEncodedImageFormat.Png);
        Assert.True(png.Length > 1_000_000, "the test picture is " + png.Length + " bytes");

        var shrunk = Shrinker.Shrink(png, Pictures.Png, null, Pictures.DefaultBytes)!;
        Assert.True(shrunk.Reduced);
        Assert.True(shrunk.Bytes.Length <= Pictures.DefaultBytes);
        Assert.Equal(Pictures.Png, Pictures.Detect(shrunk.Bytes));
        var (width, height) = Size(shrunk.Bytes);
        Assert.InRange(width, 256, 1199);
        Assert.Equal(1200.0 / 800.0, (double)width / height, precision: 1);                  // the aspect ratio is kept
    }

    [Fact]
    public void ABigJpegAndABigWebPKeepTheirTypeToo()
    {
        using var bitmap = Noise(1500, 1000);
        var jpeg = Encode(bitmap, SKEncodedImageFormat.Jpeg, 100);
        Assert.True(jpeg.Length > 1_000_000, "the test JPEG is " + jpeg.Length + " bytes");
        var shrunkJpeg = Shrinker.Shrink(jpeg, Pictures.Jpeg, null, Pictures.DefaultBytes)!;
        Assert.True(shrunkJpeg.Reduced);
        Assert.True(shrunkJpeg.Bytes.Length <= Pictures.DefaultBytes);
        Assert.Equal(Pictures.Jpeg, Pictures.Detect(shrunkJpeg.Bytes));
        Assert.True(Size(shrunkJpeg.Bytes).Width < 1500);

        var webp = Encode(bitmap, SKEncodedImageFormat.Webp, 100);
        Assert.True(webp.Length > 1_000_000, "the test WebP is " + webp.Length + " bytes");
        var shrunkWebp = Shrinker.Shrink(webp, Pictures.WebP, null, Pictures.DefaultBytes)!;
        Assert.True(shrunkWebp.Reduced);
        Assert.True(shrunkWebp.Bytes.Length <= Pictures.DefaultBytes);
        Assert.Equal(Pictures.WebP, Pictures.Detect(shrunkWebp.Bytes));
    }

    [Fact]
    public void APictureWithinTheLimitsComesBackAsTheSameBytesAndNotReduced()
    {
        using var bitmap = Noise(64, 48);
        var png = Encode(bitmap, SKEncodedImageFormat.Png);
        var kept = Shrinker.Shrink(png, Pictures.Png, 512, Pictures.DefaultBytes)!;
        Assert.False(kept.Reduced);
        Assert.Same(png, kept.Bytes);
    }

    [Fact]
    public void ASideLimitAloneMakesABigPictureSmallerEvenWhenItsBytesAreFine()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(900, 600, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        var png = Encode(bitmap, SKEncodedImageFormat.Png);
        Assert.True(png.Length < 100_000);                                                  // under every byte limit: only the side is over

        var shrunk = Shrinker.Shrink(png, Pictures.Png, 300, Pictures.DefaultBytes)!;
        Assert.True(shrunk.Reduced);
        Assert.Equal((300, 200), Size(shrunk.Bytes));
        Assert.Equal(Pictures.Png, Pictures.Detect(shrunk.Bytes));
    }

    [Fact]
    public void APngKeepsItsTransparency()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(800, 800, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(new SKColor(10, 200, 30, 0));                                           // fully transparent
        var png = Encode(bitmap, SKEncodedImageFormat.Png);
        var shrunk = Shrinker.Shrink(png, Pictures.Png, 400, Pictures.DefaultBytes)!;
        using var data = SKData.CreateCopy(shrunk.Bytes);
        using var small = SKBitmap.Decode(data);
        Assert.Equal(0, small.GetPixel(10, 10).Alpha);
        Assert.NotEqual(SKAlphaType.Opaque, small.AlphaType);
    }

    [Fact]
    public void APictureThatWouldHaveToGoUnder256PixelsAndIsStillTooBigIsDropped()
    {
        using var bitmap = Noise(300, 300);
        var png = Encode(bitmap, SKEncodedImageFormat.Png);
        Assert.True(png.Length > Pictures.MinBytes);
        // 300 px is the longest side; the next step (240) would be under 256 while 300 px of noise is still over 100,000 bytes
        Assert.Null(Shrinker.Shrink(png, Pictures.Png, null, Pictures.MinBytes));
    }

    /// <summary>A GIF of the given logical size with a comment that makes it as long as wanted; only its header is read to see how big it is.</summary>
    static byte[] Gif(int width, int height, int length)
    {
        var bytes = new List<byte>("GIF89a"u8.ToArray());
        bytes.AddRange([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x80, 0, 0]);
        bytes.AddRange([0, 0, 0, 255, 255, 255]);                                           // the two colours
        bytes.AddRange([0x21, 0xFE]);                                                       // a comment, to give the file its length
        for (var left = length - 100; left > 0;)
        {
            var block = Math.Min(255, left);
            bytes.Add((byte)block);
            bytes.AddRange(new byte[block]);
            left -= block;
        }

        bytes.Add(0);
        bytes.AddRange([0x2C, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0]);       // the picture
        bytes.AddRange([2, 2, 0x44, 0x01, 0]);
        bytes.Add(0x3B);
        return bytes.ToArray();
    }

    [Fact]
    public void AGifIsNeverChangedItIsLeftAloneWithinTheLimitsAndDroppedOverThem()
    {
        var small = Gif(40, 30, 200);
        var kept = Shrinker.Shrink(small, Pictures.Gif, 512, Pictures.DefaultBytes)!;
        Assert.False(kept.Reduced);
        Assert.Same(small, kept.Bytes);

        Assert.Null(Shrinker.Shrink(Gif(40, 30, 200), Pictures.Gif, 20, Pictures.DefaultBytes));                // over the side limit: never changed, so dropped
        Assert.Null(Shrinker.Shrink(Gif(40, 30, 1_200_000), Pictures.Gif, null, Pictures.DefaultBytes));        // over the byte limit
    }

    [Fact]
    public void BytesThatAreNoPictureThePictureLibraryCanReadAreAnError()
    {
        Assert.Throws<InvalidDataException>(() => Shrinker.Shrink(TestPictures.JpegHead(), Pictures.Jpeg, null, Pictures.DefaultBytes));
    }
}
