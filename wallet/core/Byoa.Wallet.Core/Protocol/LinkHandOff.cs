using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Byoa.Wallet.Protocol;

/// <summary>
/// A second <c>Byoa.Wallet.exe --url</c> hands its link to the wallet that is already running, over the per-user
/// pipe, and exits. Every step has a limit, so a running wallet that does not answer cannot keep the second process
/// alive: connecting is tried for a few seconds (the wallet may still be starting), and the answer is awaited for
/// <see cref="AnswerWithin"/> once the link was written (it is not written twice: the wallet ignores a repeated
/// session code anyway).
/// </summary>
public static class LinkHandOff
{
    public static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(4);

    /// <returns>True when the running wallet confirmed the link.</returns>
    public static bool Send(string pipeName, string link, TimeSpan? tryFor = null, TimeSpan? answerWithin = null, bool userActivation = true)
    {
        var frame = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["id"] = 1, ["type"] = Wire.Link, ["origin"] = "", ["userActivation"] = userActivation, ["payload"] = new JsonObject { ["url"] = link },
        }.ToJsonString());
        var until = DateTime.UtcNow + (tryFor ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(2000);
            }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= until) return false;
                Thread.Sleep(300);
                continue;
            }

            using var limit = new CancellationTokenSource(answerWithin ?? AnswerWithin);
            try
            {
                Framing.WriteAsync(pipe, frame, limit.Token).GetAwaiter().GetResult();
                return Framing.ReadAsync(pipe, Framing.MaxIncoming, limit.Token).GetAwaiter().GetResult() is not null;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
            {
                return false;
            }
        }
    }
}
