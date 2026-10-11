using System.IO.Pipes;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Serves the native host over the per-user named pipe (only the current user can open it). Each connection is
/// one browser's extension port; messages on it are handled concurrently (a request can wait for the visitor
/// while calls continue). When the connection closes, everything still waiting on it is cancelled.
/// </summary>
sealed class PipeServer : IDisposable
{
    readonly WalletEngine _engine;
    readonly CancellationTokenSource _stop = new();

    public PipeServer(WalletEngine engine) => _engine = engine;

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    async Task AcceptLoopAsync()
    {
        var name = WalletPaths.PipeName();
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var write = new SemaphoreSlim(1, 1);
        var running = new List<Task>();
        try
        {
            while (true)
            {
                var frame = await Framing.ReadAsync(pipe, Framing.MaxIncoming, connection.Token).ConfigureAwait(false);
                if (frame is null) break;
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    byte[] response;
                    var message = WireMessage.Parse(frame);
                    if (message is null)
                    {
                        if (!Wire.TryPeek(frame, out var id, out var type)) return;
                        response = Wire.Response(id, Wire.Fallback(type));
                    }
                    else
                    {
                        var result = await _engine.HandleAsync(message, connection.Token).ConfigureAwait(false);
                        response = Wire.Response(message.Id, result);
                    }

                    await write.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        await Framing.WriteAsync(pipe, response, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // the host went away
                    }
                    finally
                    {
                        write.Release();
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
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                // cancelled with the connection
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose() => _stop.Cancel();
}
