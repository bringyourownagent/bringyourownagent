using System.IO.Compression;
using System.Text;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>A model behind a key that draws: it answers text like <see cref="FakeTextSource"/> and gives one scripted picture (or none, or a failure) per request.</summary>
sealed class FakeImageSource : FakeTextSource, IImageSource, ITextRefusal
{
    public FakeImageSource(string id, decimal costPerCall = 0.004m) : base(id, Sources.Runs.ApiKey, costPerCall) { }

    /// <summary>Set when the wallet learned the model draws a picture on every text request.</summary>
    public string? RefusesText { get; private set; }

    public void RefuseText(string why) => RefusesText = why;

    /// <summary>What each request gives, in order (the last repeats); empty means one small PNG.</summary>
    public Queue<Func<ImageRequest, ImageAnswer>> Pictures { get; } = new();

    /// <summary>What the model charges a request; null means the estimate (the provider said nothing).</summary>
    public decimal? ReportedCost { get; set; }

    int _imageCalls;
    public int ImageCalls => Volatile.Read(ref _imageCalls);
    public List<ImageRequest> ImageRequests { get; } = [];

    /// <summary>When set, a request waits for it: a picture call held in flight.</summary>
    public TaskCompletionSource? HoldPicture { get; set; }

    public decimal EstimateImageCost(ImageRequest request) => CostPerCall;

    public async Task<ImageAnswer> GenerateImageAsync(ImageRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _imageCalls);
        lock (ImageRequests) ImageRequests.Add(request);
        if (HoldPicture is { } hold) await hold.Task.WaitAsync(ct);
        if (Fail) throw new TextSourceException("fake picture failure", ReportedCost, providerCalled: true, responseReceived: false);
        if (Pictures.Count == 0) return new ImageAnswer([TestPictures.Png(8, 8)], ReportedCost, "fake");
        var make = Pictures.Count > 1 ? Pictures.Dequeue() : Pictures.Peek();
        return make(request);
    }
}

/// <summary>Real pictures for the tests, made without a picture library.</summary>
static class TestPictures
{
    /// <summary>A PNG of the given size; <paramref name="noise"/> fills it with pseudo-random colour, which does not compress (a big file from a small picture).</summary>
    public static byte[] Png(int width, int height, bool noise = false)
    {
        var raw = new byte[height * (1 + width * 3)];
        var random = new Random(7);
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            raw[row] = 0;
            for (var x = 0; x < width * 3; x++) raw[row + 1 + x] = noise ? (byte)random.Next(256) : (byte)((x / 3 + y) % 256);
        }

        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;          // bits per channel
        header[9] = 2;          // truecolour
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", deflated.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>The first bytes of a JPEG (enough for the wallet to tell the type; not a picture a program can open).</summary>
    public static byte[] JpegHead(int length = 64) => new byte[length].Select((_, i) => i < 3 ? (byte)(i == 0 ? 0xFF : i == 1 ? 0xD8 : 0xFF) : (byte)i).ToArray();

    /// <summary>A GIF header and a body of the given size (a GIF the wallet never changes).</summary>
    public static byte[] Gif(int length = 64) => Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[Math.Max(0, length - 6)]).ToArray();

    public static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    public static string DataUrl(byte[] bytes, string mime = "image/png") => "data:" + mime + ";base64," + Convert.ToBase64String(bytes);

    static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(body);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(body));
        stream.Write(crc);
    }

    static uint Crc(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return ~crc;
    }
}
