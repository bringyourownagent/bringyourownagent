using System.IO.Pipes;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Serves the native host over the per-user named pipe (only the current user can open it). Each connection is
/// one browser's extension port, served by a <see cref="FrameServer"/>: messages run concurrently (a request can
/// wait for the visitor while calls continue), with a bound and backpressure. When the connection closes,
/// everything still waiting on it is cancelled.
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
        try
        {
            // At most FrameServer.DefaultParallel messages of one connection run at once; then the pipe waits.
            await new FrameServer(_engine.HandleAsync).ServeAsync(pipe, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose() => _stop.Cancel();
}
