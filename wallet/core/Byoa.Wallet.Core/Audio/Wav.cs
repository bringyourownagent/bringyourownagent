using System.Buffers.Binary;
using System.Text;

namespace Byoa.Wallet.Audio;

/// <summary>One decoded clip: mono samples in -1..1 at <see cref="SampleRate"/>, and what the file declared.</summary>
public sealed record WavClip(float[] Samples, int SampleRate, int Channels, int BitsPerSample)
{
    /// <summary>The length of the clip in seconds, measured from the audio data itself.</summary>
    public double Seconds => SampleRate == 0 ? 0 : (double)Samples.Length / SampleRate;
}

/// <summary>
/// Reads the WAV clips pages send (the contract asks for 16 kHz mono 16-bit PCM; 8-bit, 24-bit, 32-bit and float,
/// other rates and stereo are accepted and converted). The wallet measures the length from the data, never from
/// anything the page says.
/// </summary>
public static class Wav
{
    public const int TargetRate = 16000;

    /// <summary>Decodes a RIFF/WAVE file into mono float samples.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a WAV file this reader supports.</exception>
    public static WavClip Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !Tag(bytes, 0, "RIFF") || !Tag(bytes, 8, "WAVE"))
            throw new InvalidDataException("not a RIFF/WAVE file");

        int format = 0, channels = 0, rate = 0, bits = 0;
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes.Slice(at, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at + 4, 4));
            var body = at + 8;
            if (id == "fmt ")
            {
                if (size < 16 || body + 16 > bytes.Length) throw new InvalidDataException("short fmt chunk");
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 14, 2));
                if (format == 0xFFFE && size >= 40 && body + 26 <= bytes.Length)
                {
                    // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                    format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 24, 2));
                }
            }
            else if (id == "data")
            {
                if (channels == 0) throw new InvalidDataException("data before fmt");
                // A streamed WAV may declare a bigger (or "unknown", 0xFFFFFFFF) size than it has: use what is there.
                var length = (int)Math.Min(size, (uint)(bytes.Length - body));
                return new WavClip(Decode(bytes.Slice(body, length), format, channels, rate, bits), rate, channels, bits);
            }

            // 64-bit arithmetic: a chunk that claims to run past the end of the file is an error, not an overflow.
            var next = (long)body + size + (size & 1);
            if (next > bytes.Length) throw new InvalidDataException($"the {id.Trim()} chunk runs past the end of the file");
            at = (int)next;
        }

        throw new InvalidDataException("no data chunk");
    }

    /// <summary>Reads a clip and returns 16 kHz mono samples, the input every local model of 0.1 expects.</summary>
    public static WavClip ParseAs16k(ReadOnlySpan<byte> bytes)
    {
        var clip = Parse(bytes);
        return clip.SampleRate == TargetRate ? clip : clip with { Samples = Resample(clip.Samples, clip.SampleRate, TargetRate), SampleRate = TargetRate };
    }

    static bool Tag(ReadOnlySpan<byte> bytes, int at, string tag) => Encoding.ASCII.GetString(bytes.Slice(at, 4)) == tag;

    static float[] Decode(ReadOnlySpan<byte> data, int format, int channels, int rate, int bits)
    {
        if (channels < 1 || channels > 8) throw new InvalidDataException($"{channels} channels");
        if (rate < 4000 || rate > 192000) throw new InvalidDataException($"sample rate {rate}");
        var pcm = format == 1;
        var ieee = format == 3;
        if (!pcm && !ieee) throw new InvalidDataException($"WAV format {format} (only PCM and IEEE float)");
        if (pcm && bits is not (8 or 16 or 24 or 32)) throw new InvalidDataException($"{bits}-bit PCM");
        if (ieee && bits != 32) throw new InvalidDataException($"{bits}-bit float");

        var bytesPerSample = bits / 8;
        var frameBytes = bytesPerSample * channels;
        var frames = data.Length / frameBytes;
        var result = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                var s = data.Slice(i * frameBytes + c * bytesPerSample, bytesPerSample);
                sum += ieee ? BinaryPrimitives.ReadSingleLittleEndian(s)
                    : bits switch
                    {
                        8 => (s[0] - 128) / 128f,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                        24 => ((s[0] | (s[1] << 8) | ((sbyte)s[2] << 16))) / 8388608f,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                    };
            }

            var v = sum / channels;
            result[i] = float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f;
        }

        return result;
    }

    /// <summary>Linear resampling; enough for speech going to a recognizer.</summary>
    public static float[] Resample(float[] samples, int fromRate, int toRate)
    {
        if (fromRate == toRate || samples.Length == 0) return samples;
        var length = (int)((long)samples.Length * toRate / fromRate);
        var result = new float[length];
        var ratio = (double)fromRate / toRate;
        for (var i = 0; i < length; i++)
        {
            var at = i * ratio;
            var i0 = (int)at;
            var i1 = Math.Min(i0 + 1, samples.Length - 1);
            var frac = (float)(at - i0);
            result[i] = samples[i0] * (1 - frac) + samples[i1] * frac;
        }

        return result;
    }

    /// <summary>Encodes 16-bit mono PCM WAV (used when a clip goes on to a cloud source).</summary>
    public static byte[] Encode(float[] samples, int rate)
    {
        var dataSize = samples.Length * 2;
        var bytes = new byte[44 + dataSize];
        var span = bytes.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataSize);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], (short)Math.Clamp(samples[i] * 32767f, -32768f, 32767f));
        }

        return bytes;
    }
}
