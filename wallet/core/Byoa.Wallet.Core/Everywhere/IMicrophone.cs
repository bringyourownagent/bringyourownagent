namespace Byoa.Wallet.Everywhere;

/// <summary>The microphone could not be used; the message is for the visitor ("No microphone was found").</summary>
public sealed class MicrophoneException(string message) : Exception(message);

/// <summary>The default recording device, one recording at a time.</summary>
public interface IMicrophone
{
    /// <summary>Starts recording 16 kHz mono; nothing is kept beyond <paramref name="maxSeconds"/>.</summary>
    /// <exception cref="MicrophoneException"/>
    void Start(int maxSeconds);

    /// <summary>Stops and returns what was recorded, as samples in -1..1 at 16 kHz.</summary>
    /// <exception cref="MicrophoneException"/>
    float[] Stop();

    /// <summary>Stops and throws the recording away.</summary>
    void Cancel();

    /// <summary>Raised when the device stops working during a recording (unplugged, taken away); the recording is lost.</summary>
    event Action<string>? Failed;
}
