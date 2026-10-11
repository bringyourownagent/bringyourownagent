using System.Text.Json.Nodes;

namespace Byoa.Wallet.Protocol;

/// <summary>
/// Serves one connection of framed messages (the native host's pipe): each message is handled on its own task so
/// that a request waiting for the visitor does not hold up calls, but at most <see cref="MaxParallel"/> at once.
/// When that many are running, the next frame is not read until one finishes, so the sender is held back by the
/// pipe itself (backpressure) instead of the wallet buffering frames without limit.
/// </summary>
public sealed class FrameServer
{
    public const int DefaultParallel = 16;

    readonly Func<WireMessage, CancellationToken, Task<JsonNode?>> _handle;

    public FrameServer(Func<WireMessage, CancellationToken, Task<JsonNode?>> handle, int maxParallel = DefaultParallel)
    {
        _handle = handle;
        MaxParallel = maxParallel;
    }

    public int MaxParallel { get; }

    /// <summary>Runs until the stream ends or <paramref name="ct"/> is cancelled; everything still running is then cancelled.</summary>
    public async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(MaxParallel, MaxParallel);
        var write = new SemaphoreSlim(1, 1);
        var running = new List<Task>();
        try
        {
            while (true)
            {
                await slots.WaitAsync(connection.Token).ConfigureAwait(false);
                byte[]? frame;
                try
                {
                    frame = await Framing.ReadAsync(stream, Framing.MaxIncoming, connection.Token).ConfigureAwait(false);
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                if (frame is null)
                {
                    slots.Release();
                    break;
                }

                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        await AnswerAsync(stream, frame, write, connection.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            // the host closed the pipe or broke the framing
        }
        finally
        {
            connection.Cancel();
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // cancelled with the connection
            }
        }
    }

    async Task AnswerAsync(Stream stream, byte[] frame, SemaphoreSlim write, CancellationToken ct)
    {
        byte[] response;
        var message = WireMessage.Parse(frame);
        if (message is null)
        {
            // Not a message the wallet can read: still answer its id with the fallback, so nothing waits for nothing.
            if (!Wire.TryPeek(frame, out var id, out var type)) return;
            response = Wire.Response(id, Wire.Fallback(type));
        }
        else
        {
            JsonNode? result;
            try
            {
                result = await _handle(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = Wire.Fallback(message.Type);
            }

            response = Wire.Response(message.Id, result);
        }

        await write.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Framing.WriteAsync(stream, response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // the host went away
        }
        finally
        {
            write.Release();
        }
    }
}
