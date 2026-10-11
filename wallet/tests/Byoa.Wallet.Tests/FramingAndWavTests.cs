using System.Buffers.Binary;
using System.Text;
using Byoa.Wallet.Audio;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Tests;

public class FramingTests
{
    [Fact]
    public async Task RoundTripsSeveralMessages()
    {
        var stream = new MemoryStream();
        await Framing.WriteAsync(stream, Encoding.UTF8.GetBytes("{\"id\":1}"));
        await Framing.WriteAsync(stream, Encoding.UTF8.GetBytes("{\"id\":2,\"text\":\"ščž\"}"));
        stream.Position = 0;
        Assert.Equal("{\"id\":1}", Encoding.UTF8.GetString((await Framing.ReadAsync(stream))!));
        Assert.Equal("{\"id\":2,\"text\":\"ščž\"}", Encoding.UTF8.GetString((await Framing.ReadAsync(stream))!));
        Assert.Null(await Framing.ReadAsync(stream));
    }

    [Fact]
    public async Task LengthIsFourBytesLittleEndian()
    {
        var stream = new MemoryStream();
        await Framing.WriteAsync(stream, new byte[300]);
        var bytes = stream.ToArray();
        Assert.Equal(304, bytes.Length);
        Assert.Equal(new byte[] { 0x2C, 0x01, 0, 0 }, bytes[..4]);
    }

    [Fact]
    public async Task EndInsideTheHeaderOrBodyIsAnError()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadAsync(new MemoryStream([5, 0])));
        var body = new byte[4 + 3];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 10);
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => Framing.ReadAsync(new MemoryStream(body)));
    }

    [Fact]
    public async Task OversizedMessagesAreRefusedBeforeReadingThem()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Framing.MaxIncoming + 1u);
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadAsync(new MemoryStream(header)));
    }

    [Fact]
    public void ResponseCarriesTheIdAndTheResult()
    {
        var bytes = Wire.Response(System.Text.Json.Nodes.JsonValue.Create(7), null);
        Assert.Equal("{\"id\":7,\"result\":null}", Encoding.UTF8.GetString(bytes));
        Assert.True(Wire.TryPeek(bytes, out var id, out var type));
        Assert.Equal("7", id!.ToJsonString());
        Assert.Null(type);
    }

    [Fact]
    public void MessagesParseAndBadOnesAreNull()
    {
        var m = WireMessage.Parse("{\"id\":3,\"type\":\"call\",\"origin\":\"https://a.example\",\"userActivation\":true,\"payload\":{\"grantId\":\"g\"}}"u8);
        Assert.NotNull(m);
        Assert.Equal("call", m!.Type);
        Assert.Equal("https://a.example", m.Origin);
        Assert.True(m.UserActivation);
        Assert.Null(WireMessage.Parse("not json"u8));
        Assert.Null(WireMessage.Parse("[1,2]"u8));
        Assert.False(WireMessage.Parse("{\"type\":\"request\",\"userActivation\":\"yes\"}"u8)!.UserActivation);
    }
}

public class WavTests
{
    static byte[] Pcm(int rate, int channels, int bits, int frames, int format = 1)
    {
        var bytesPerSample = bits / 8;
        var data = frames * channels * bytesPerSample;
        var b = new byte[44 + data];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 36 + data);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(20), (short)format);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), rate * channels * bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), (short)(channels * bytesPerSample));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(34), (short)bits);
        Encoding.ASCII.GetBytes("data").CopyTo(b, 36);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), data);
        return b;
    }

    [Theory]
    [InlineData(16000, 1, 16, 32000, 2.0)]
    [InlineData(16000, 1, 16, 240000, 15.0)]
    [InlineData(44100, 2, 16, 44100 * 3, 3.0)]
    [InlineData(48000, 1, 24, 24000, 0.5)]
    [InlineData(8000, 1, 8, 8000, 1.0)]
    public void MeasuresTheLengthFromTheData(int rate, int channels, int bits, int frames, double seconds)
    {
        var clip = Wav.Parse(Pcm(rate, channels, bits, frames));
        Assert.Equal(seconds, clip.Seconds, 3);
        Assert.Equal(frames, clip.Samples.Length);
    }

    [Fact]
    public void ConvertsTo16kHzMono()
    {
        var clip = Wav.ParseAs16k(Pcm(48000, 2, 16, 48000));
        Assert.Equal(16000, clip.SampleRate);
        Assert.Equal(16000, clip.Samples.Length);
        Assert.Equal(1.0, clip.Seconds, 3);
    }

    [Fact]
    public void ADeclaredSizeLargerThanTheDataIsNotTrusted()
    {
        var wav = Pcm(16000, 1, 16, 16000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), int.MaxValue);
        Assert.Equal(1.0, Wav.Parse(wav).Seconds, 3);
    }

    [Fact]
    public void ReadsFloatWavs()
    {
        var wav = Pcm(16000, 1, 32, 16000, format: 3);
        BinaryPrimitives.WriteSingleLittleEndian(wav.AsSpan(44), 0.5f);
        var clip = Wav.Parse(wav);
        Assert.Equal(0.5f, clip.Samples[0]);
    }

    [Fact]
    public void EncodeAndParseRoundTrip()
    {
        var samples = Enumerable.Range(0, 1600).Select(i => (float)Math.Sin(i / 10.0) * 0.5f).ToArray();
        var clip = Wav.Parse(Wav.Encode(samples, 16000));
        Assert.Equal(0.1, clip.Seconds, 3);
        Assert.InRange(Math.Abs(clip.Samples[100] - samples[100]), 0, 0.001);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RIFF....WAVE")]
    [InlineData("not a wav file at all, just text")]
    public void RejectsWhatIsNotAWav(string text)
    {
        Assert.Throws<InvalidDataException>(() => Wav.Parse(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void RejectsCompressedFormats()
    {
        Assert.Throws<InvalidDataException>(() => Wav.Parse(Pcm(16000, 1, 16, 100, format: 0x55)));
    }
}
