using System.IO.Pipes;
using System.Text.Json.Nodes;
using Byoa.Wallet.Desktop;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;

namespace Byoa.Wallet.Tests;

public sealed class AgentPipeTests
{
    [Fact]
    public async Task UnapprovedClientIsRefusedAndFullAccessIsGated()
    {
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        var oldMcp = Environment.GetEnvironmentVariable("BYOA_WALLET_MCP_PATH");
        var home = TestFolder.New();
        var bridgeFolder = Path.Combine(home, "bridge");
        Directory.CreateDirectory(bridgeFolder);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        Environment.SetEnvironmentVariable("BYOA_WALLET_MCP_PATH", bridgeFolder);
        try
        {
            using var wallet = new WalletServices();
            var consent = new FakeConsentUi(new AgentConsentAnswer(AgentConsentDecision.NotNow, false));
            using var process = new FakeProcessInfo(
                new ObservedAgentProcess(Path.Combine(bridgeFolder, "byoa-mcp.exe"), "C:\\agents\\first.exe"),
                new ObservedAgentProcess(Path.Combine(bridgeFolder, "byoa-mcp.exe"), "C:\\agents\\normal.exe"),
                new ObservedAgentProcess(Path.Combine(bridgeFolder, "byoa-mcp.exe"), "C:\\agents\\full.exe"));
            using var server = new AgentPipeServer(wallet, consent, process);
            server.Start();

            await using (var denied = await FakePipeClient.ConnectAsync("unapproved test agent"))
            {
                var hello = await denied.HelloAsync();
                Assert.False(hello["ok"]!.GetValue<bool>());
                Assert.Contains("Not now", hello["error"]!.GetValue<string>(), StringComparison.Ordinal);
                var refused = await denied.CallAsync("status");
                Assert.False(refused["ok"]!.GetValue<bool>());
                Assert.Contains("Not now", refused["error"]!.GetValue<string>(), StringComparison.Ordinal);
            }

            consent.Answer = new AgentConsentAnswer(AgentConsentDecision.Allow, false);
            await using (var normal = await FakePipeClient.ConnectAsync("normal test agent"))
            {
                var hello = await normal.HelloAsync();
                Assert.True(hello["ok"]!.GetValue<bool>());
                Assert.False(hello["fullAccess"]!.GetValue<bool>());
                var gated = await normal.CallAsync("list_sites");
                Assert.False(gated["ok"]!.GetValue<bool>());
                Assert.Contains("Full access", gated["error"]!.GetValue<string>(), StringComparison.Ordinal);
            }

            consent.Answer = new AgentConsentAnswer(AgentConsentDecision.Allow, true);
            await using (var full = await FakePipeClient.ConnectAsync("full access test agent"))
            {
                var hello = await full.HelloAsync();
                Assert.True(hello["ok"]!.GetValue<bool>());
                Assert.True(hello["fullAccess"]!.GetValue<bool>());
                var sites = await full.CallAsync("list_sites");
                Assert.True(sites["ok"]!.GetValue<bool>());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
            Environment.SetEnvironmentVariable("BYOA_WALLET_MCP_PATH", oldMcp);
        }
    }

    [Fact]
    public async Task AHelloClaimCannotBorrowAnApprovedParentIdentity()
    {
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        var oldMcp = Environment.GetEnvironmentVariable("BYOA_WALLET_MCP_PATH");
        var home = TestFolder.New();
        var bridgeFolder = Path.Combine(home, "bridge");
        Directory.CreateDirectory(bridgeFolder);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        Environment.SetEnvironmentVariable("BYOA_WALLET_MCP_PATH", bridgeFolder);
        try
        {
            using var wallet = new WalletServices();
            var consent = new FakeConsentUi(new AgentConsentAnswer(AgentConsentDecision.Allow, false));
            using var process = new FakeProcessInfo(
                new ObservedAgentProcess(Path.Combine(bridgeFolder, "byoa-mcp.exe"), "C:\\agents\\approved.exe"),
                new ObservedAgentProcess(Path.Combine(bridgeFolder, "byoa-mcp.exe"), "C:\\agents\\impostor.exe"));
            using var server = new AgentPipeServer(wallet, consent, process);
            server.Start();

            await using (var first = await FakePipeClient.ConnectAsync("same announced name"))
                Assert.True((await first.HelloAsync())["ok"]!.GetValue<bool>());

            consent.Answer = new AgentConsentAnswer(AgentConsentDecision.NotNow, false);
            await using (var second = await FakePipeClient.ConnectAsync("same announced name"))
            {
                var hello = await second.HelloAsync();
                Assert.False(hello["ok"]!.GetValue<bool>());
                Assert.Contains("Not now", hello["error"]!.GetValue<string>(), StringComparison.Ordinal);
            }

            Assert.Equal(2, consent.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
            Environment.SetEnvironmentVariable("BYOA_WALLET_MCP_PATH", oldMcp);
        }
    }

    [Fact]
    public async Task AClientOutsideTheWalletBridgeFolderIsRefused()
    {
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        var oldMcp = Environment.GetEnvironmentVariable("BYOA_WALLET_MCP_PATH");
        var home = TestFolder.New();
        var bridgeFolder = Path.Combine(home, "bridge");
        Directory.CreateDirectory(bridgeFolder);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        Environment.SetEnvironmentVariable("BYOA_WALLET_MCP_PATH", bridgeFolder);
        try
        {
            using var wallet = new WalletServices();
            using var process = new FakeProcessInfo(new ObservedAgentProcess("C:\\temporary\\byoa-mcp.exe", "C:\\agents\\wrapper.exe"));
            using var server = new AgentPipeServer(wallet, new FakeConsentUi(new AgentConsentAnswer(AgentConsentDecision.Allow, false)), process);
            server.Start();

            await using var client = await FakePipeClient.ConnectAsync("not the bridge");
            var hello = await client.HelloAsync();
            Assert.False(hello["ok"]!.GetValue<bool>());
            Assert.Contains("not the wallet's MCP bridge", hello["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
            Environment.SetEnvironmentVariable("BYOA_WALLET_MCP_PATH", oldMcp);
        }
    }

    sealed class FakeConsentUi(AgentConsentAnswer answer) : IAgentConsentUi
    {
        public AgentConsentAnswer Answer { get; set; } = answer;
        public int Calls { get; private set; }
        public Task<AgentConsentAnswer> AskAgentAsync(AgentConsentModel model, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }

    sealed class FakeProcessInfo(params ObservedAgentProcess[] observations) : IAgentProcessInfo, IDisposable
    {
        readonly Queue<ObservedAgentProcess> _observations = new(observations);
        public ObservedAgentProcess Read(Microsoft.Win32.SafeHandles.SafePipeHandle pipe) =>
            _observations.Count > 0 ? _observations.Dequeue() : observations[^1];
        public void Dispose() { }
    }

    sealed class FakePipeClient : IAsyncDisposable
    {
        readonly NamedPipeClientStream _pipe;
        readonly string _name;

        FakePipeClient(NamedPipeClientStream pipe, string name)
        {
            _pipe = pipe;
            _name = name;
        }

        public static async Task<FakePipeClient> ConnectAsync(string name)
        {
            var pipe = new NamedPipeClientStream(".", WalletPaths.McpPipeName(), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            return new FakePipeClient(pipe, name);
        }

        public async Task<JsonObject> HelloAsync()
        {
            await Framing.WriteAsync(_pipe, AgentPipeProtocol.Serialize(new JsonObject
            {
                ["type"] = AgentPipeProtocol.Hello,
                ["name"] = _name,
                ["executablePath"] = "C:\\test\\agent.exe",
            }));
            return await ReadAsync();
        }

        public async Task<JsonObject> CallAsync(string operation)
        {
            await Framing.WriteAsync(_pipe, AgentPipeProtocol.Serialize(new JsonObject
            {
                ["type"] = AgentPipeProtocol.Request,
                ["id"] = Guid.NewGuid().ToString("N"),
                ["operation"] = operation,
                ["arguments"] = new JsonObject(),
            }));
            return await ReadAsync();
        }

        async Task<JsonObject> ReadAsync()
        {
            var bytes = await Framing.ReadAsync(_pipe, 256 * 1024);
            return Assert.IsType<JsonObject>(AgentPipeProtocol.Parse(bytes!));
        }

        public ValueTask DisposeAsync() => _pipe.DisposeAsync();
    }
}
