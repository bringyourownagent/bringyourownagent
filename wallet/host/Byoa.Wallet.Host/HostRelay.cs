using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Host;

/// <summary>Opens a stream to the running wallet, optionally starting it first.</summary>
public interface IWalletConnector
{
    /// <returns>A connected duplex stream, or null when the wallet is not running (and was not or could not be started).</returns>
    Task<Stream?> ConnectAsync(bool startIfNeeded, CancellationToken ct);
}

/// <summary>
/// The native host's work: messages from the extension (stdin) go to the wallet's pipe as they are, answers come
/// back to stdout. The host decides nothing about pages; it only starts the wallet for a <c>request</c> or an
/// <c>open</c>, and answers the fallback (null, <c>{ ok: false }</c>, "not running") when the wallet is not there.
/// </summary>
public sealed class HostRelay
{
    readonly Stream _browserIn;
    readonly Stream _browserOut;
    readonly IWalletConnector _connector;
    readonly SemaphoreSlim _writeBrowser = new(1, 1);
    readonly SemaphoreSlim _writeWallet = new(1, 1);
    readonly SemaphoreSlim _connect = new(1, 1);
    readonly ConcurrentDictionary<string, (JsonNode? Id, string? Type)> _pending = new(StringComparer.Ordinal);
    Stream? _wallet;

    public HostRelay(Stream browserIn, Stream browserOut, IWalletConnector connector)
    {
        _browserIn = browserIn;
        _browserOut = browserOut;
        _connector = connector;
    }

    /// <summary>Runs until the browser closes the port (stdin ends).</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            var frame = await Framing.ReadAsync(_browserIn, Framing.MaxIncoming, ct).ConfigureAwait(false);
            if (frame is null) break;
            if (!Wire.TryPeek(frame, out var id, out var type)) continue;
            if (!Wire.FromBrowser(type))
            {
                // Only the extension's message types go to the wallet (a "link" comes only from the wallet's own exe).
                await AnswerAsync(id, Wire.Fallback(type), ct).ConfigureAwait(false);
                continue;
            }

            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(ForwardAsync(frame, id, type, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    async Task ForwardAsync(byte[] frame, JsonNode? id, string? type, CancellationToken ct)
    {
        var start = type is Wire.Request or Wire.Open;
        var wallet = await EnsureWalletAsync(start, ct).ConfigureAwait(false);
        if (wallet is null)
        {
            await AnswerAsync(id, type == Wire.Status ? NotRunning() : Wire.Fallback(type), ct).ConfigureAwait(false);
            return;
        }

        var key = Key(id);
        _pending[key] = (id, type);
        try
        {
            await _writeWallet.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Framing.WriteAsync(wallet, frame, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeWallet.Release();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The wallet's pipe broke or was already closed by the reader: the page hears the same as with no wallet.
            if (_pending.TryRemove(key, out _)) await AnswerAsync(id, type == Wire.Status ? NotRunning() : Wire.Fallback(type), ct).ConfigureAwait(false);
            Drop(wallet);
        }
    }

    static JsonNode NotRunning() => new JsonObject { ["running"] = false, ["host"] = WalletPaths.Version };

    async Task<Stream?> EnsureWalletAsync(bool start, CancellationToken ct)
    {
        if (_wallet is { } open) return open;
        await _connect.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_wallet is { } again) return again;
            var stream = await _connector.ConnectAsync(start, ct).ConfigureAwait(false);
            if (stream is null) return null;
            _wallet = stream;
            _ = Task.Run(() => ReadWalletAsync(stream, ct), CancellationToken.None);
            return stream;
        }
        finally
        {
            _connect.Release();
        }
    }

    async Task ReadWalletAsync(Stream wallet, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var frame = await Framing.ReadAsync(wallet, Framing.MaxIncoming, ct).ConfigureAwait(false);
                if (frame is null) break;
                if (!Wire.TryPeek(frame, out var id, out _)) continue;
                if (!_pending.TryRemove(Key(id), out var pending)) continue;
                if (frame.Length > Framing.MaxToBrowser)
                {
                    await AnswerAsync(id, Wire.Fallback(pending.Type), ct).ConfigureAwait(false);
                    continue;
                }

                await WriteBrowserAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            // the wallet went away
        }

        Drop(wallet);
        // Everything still waiting gets the same answer as "no wallet".
        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var p))
            {
                await AnswerAsync(p.Id, p.Type == Wire.Status ? NotRunning() : Wire.Fallback(p.Type), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    void Drop(Stream wallet)
    {
        if (ReferenceEquals(_wallet, wallet)) _wallet = null;
        wallet.Dispose();
    }

    Task AnswerAsync(JsonNode? id, JsonNode? result, CancellationToken ct) => WriteBrowserAsync(Wire.Response(id, result), ct);

    async Task WriteBrowserAsync(byte[] frame, CancellationToken ct)
    {
        await _writeBrowser.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Framing.WriteAsync(_browserOut, frame, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // the browser closed the port
        }
        finally
        {
            _writeBrowser.Release();
        }
    }

    static string Key(JsonNode? id) => id?.ToJsonString() ?? "null";
}
