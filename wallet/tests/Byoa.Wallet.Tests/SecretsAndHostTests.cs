using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Byoa.Wallet.Host;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Tests;

/// <summary>
/// Files the tests write go under BYOA_TEST_TMP when it is set, otherwise next to the test assembly
/// (bin/.../test-files), never into the system's temp folder.
/// </summary>
static class TestFolder
{
    public static string New()
    {
        var root = Environment.GetEnvironmentVariable("BYOA_TEST_TMP");
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(AppContext.BaseDirectory, "test-files");
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public class SecretStoreTests
{
    [Fact]
    public void KeysRoundTripThroughDpapiAndAreNeverPlainOnDisk()
    {
        var dir = TestFolder.New();
        var file = Path.Combine(dir, "secrets.dat");
        const string secret = "test-secret-0123456789abcdef";
        var store = new SecretStore(file);
        Assert.False(store.Has("openrouter"));
        store.Set("openrouter", "  " + secret + "\n");
        Assert.True(store.Has("openrouter"));
        Assert.Equal(secret, new SecretStore(file).Get("openrouter"));   // a new instance reads the file back
        var raw = File.ReadAllBytes(file);
        Assert.DoesNotContain("test-secret", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("test-secret", Encoding.Unicode.GetString(raw));
        store.Remove("openrouter");
        Assert.Null(new SecretStore(file).Get("openrouter"));

        var keyFile = Path.Combine(dir, "key.txt");
        File.WriteAllText(keyFile, "\n" + secret + "\n");
        Assert.True(store.ImportFromFile("openrouter", keyFile));
        Assert.Equal(secret, store.Get("openrouter"));
        File.WriteAllText(keyFile, "\n \n");
        Assert.False(store.ImportFromFile("other", keyFile));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void SettingsSurviveARestart()
    {
        var file = Path.Combine(TestFolder.New(), "settings.json");
        var store = new SettingsStore(file);
        store.Update(s =>
        {
            Byoa.Wallet.Sources.Router.EnsureDefaults(s);
            s.Sites.Add(new SiteRecord { Origin = "https://a.example", Status = SiteStatus.Blocked });
            s.Source("gigaam-v3").Enabled = false;
        });
        var again = new SettingsStore(file);
        Assert.Equal(SiteStatus.Blocked, again.Read(s => s.Site("https://a.example")!.Status));
        Assert.False(again.Read(s => s.Source("gigaam-v3").Enabled));
        Assert.Contains("\"status\": \"Blocked\"", File.ReadAllText(file));
    }
}

/// <summary>The native host relay over real (anonymous) pipes, with an in-process fake wallet.</summary>
public class HostRelayTests
{
    /// <summary>One end of a duplex channel made of two anonymous pipes.</summary>
    sealed class Duplex(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => input.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => output.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
                output.Dispose();
            }
        }
    }

    static (Stream A, Stream B) Pair()
    {
        var aToB = new AnonymousPipeServerStream(PipeDirection.Out);
        var bFromA = new AnonymousPipeClientStream(PipeDirection.In, aToB.ClientSafePipeHandle);
        var bToA = new AnonymousPipeServerStream(PipeDirection.Out);
        var aFromB = new AnonymousPipeClientStream(PipeDirection.In, bToA.ClientSafePipeHandle);
        return (new Duplex(aFromB, aToB), new Duplex(bFromA, bToA));
    }

    sealed class Connector(Func<bool, Stream?> connect) : IWalletConnector
    {
        public List<bool> Starts { get; } = [];

        public Task<Stream?> ConnectAsync(bool startIfNeeded, CancellationToken ct)
        {
            Starts.Add(startIfNeeded);
            return Task.FromResult(connect(startIfNeeded));
        }
    }

    static byte[] Message(int id, string type) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["type"] = type, ["origin"] = "https://a.example", ["userActivation"] = false, ["payload"] = new JsonObject() }.ToJsonString());

    static async Task<JsonObject> ReadAsync(Stream s) =>
        (JsonObject)JsonNode.Parse((await Framing.ReadAsync(s).WaitAsync(TimeSpan.FromSeconds(10)))!)!;

    [Fact]
    public async Task WithoutAWalletTheHostAnswersTheFallbacks()
    {
        var (browser, host) = Pair();
        var connector = new Connector(_ => null);
        var relay = Task.Run(() => new HostRelay(host, host, connector).RunAsync());
        await Framing.WriteAsync(browser, Message(1, "status"));
        Assert.Equal("{\"id\":1,\"result\":{\"running\":false,\"host\":\"0.1.0\"}}", (await ReadAsync(browser)).ToJsonString());
        await Framing.WriteAsync(browser, Message(2, "call"));
        Assert.Equal("{\"id\":2,\"result\":{\"ok\":false}}", (await ReadAsync(browser)).ToJsonString());
        await Framing.WriteAsync(browser, Message(3, "request"));
        Assert.Equal("{\"id\":3,\"result\":null}", (await ReadAsync(browser)).ToJsonString());
        Assert.Equal([false, false, true], connector.Starts);         // only a request may start the wallet
        browser.Dispose();
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MessagesAreRelayedBothWaysAndAWalletThatGoesAwayAnswersNull()
    {
        var (browser, host) = Pair();
        var (hostSide, walletSide) = Pair();
        var connector = new Connector(_ => hostSide);
        var relay = Task.Run(() => new HostRelay(host, host, connector).RunAsync());

        await Framing.WriteAsync(browser, Message(7, "call"));
        var atWallet = (JsonObject)JsonNode.Parse((await Framing.ReadAsync(walletSide))!)!;
        Assert.Equal("https://a.example", atWallet["origin"]!.GetValue<string>());
        await Framing.WriteAsync(walletSide, Encoding.UTF8.GetBytes("{\"id\":7,\"result\":{\"ok\":true,\"output\":{\"text\":\"Peru\"}}}"));
        Assert.Equal("Peru", (await ReadAsync(browser))["result"]!["output"]!["text"]!.GetValue<string>());

        // A request is waiting for the visitor when the wallet quits: the page gets null, as with no wallet.
        await Framing.WriteAsync(browser, Message(8, "request"));
        await Framing.ReadAsync(walletSide);
        walletSide.Dispose();
        Assert.Equal("{\"id\":8,\"result\":null}", (await ReadAsync(browser)).ToJsonString());
        browser.Dispose();
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
