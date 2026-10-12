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
        var home = TestFolder.New();
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        try
        {
            using var wallet = new WalletServices();
            var consent = new FakeConsentUi(new AgentConsentAnswer(AgentConsentDecision.NotNow, false));
            using var server = new AgentPipeServer(wallet, consent);
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
        }
    }

    sealed class FakeConsentUi(AgentConsentAnswer answer) : IAgentConsentUi
    {
        public AgentConsentAnswer Answer { get; set; } = answer;
        public Task<AgentConsentAnswer> AskAgentAsync(AgentConsentModel model, CancellationToken ct) => Task.FromResult(Answer);
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
