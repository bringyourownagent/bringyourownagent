using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Byoa.Wallet.Mcp;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Tests;

public sealed class McpProtocolTests
{
    [Fact]
    public async Task HelpWorksWithoutWalletAndUnknownNotificationsGetNoReply()
    {
        var responses = await Run(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"unknown\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"help\",\"method\":\"tools/call\",\"params\":{\"name\":\"help\",\"arguments\":{}}}");

        var response = Assert.Single(responses);
        Assert.Equal("help", response["id"]!.GetValue<string>());
        Assert.True(response["result"]!["isError"]!.GetValue<bool>() == false);
        Assert.Contains("BYOA", response["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestIdsRemainStringNumberAndNull()
    {
        var responses = await Run(
            "{\"jsonrpc\":\"2.0\",\"id\":\"s\",\"method\":\"ping\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}");

        Assert.Equal(3, responses.Length);
        Assert.Contains(responses, response => response["id"]?.GetValue<string>() == "s");
        Assert.Contains(responses, response => response["id"] is JsonValue value && value.TryGetValue<int>(out var id) && id == 7);
        Assert.Contains(responses, response => response["id"] is null);
    }

    [Fact]
    public async Task ArraysAndWrongToolArgumentsAreExplicitErrors()
    {
        var responses = await Run(
            "[ {\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"} ]",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"help\",\"arguments\":[]}}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"not_a_tool\",\"arguments\":{}}}");

        Assert.Equal(3, responses.Length);
        Assert.Contains(responses, response => response["error"] is JsonObject error && error["code"]!.GetValue<int>() == -32600);
        Assert.Contains(responses, response => response["error"] is JsonObject error && error["code"]!.GetValue<int>() == -32602);
        Assert.Contains(responses, response => response["result"] is JsonObject result && result["isError"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task NonHelpToolSaysWalletIsAbsent()
    {
        var responses = await Run(
            "{\"jsonrpc\":\"2.0\",\"id\":\"status\",\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");

        var result = Assert.Single(responses)["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("not running", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullParamsAndArgumentsMeanAbsentObjects()
    {
        var responses = await Run(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":null}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"help\",\"arguments\":null}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"still-unknown\",\"params\":null}");

        Assert.Equal(2, responses.Length);
        Assert.Contains(responses, response => response["id"]?.GetValue<int>() == 1);
        Assert.Contains(responses, response => response["id"]?.GetValue<int>() == 2 &&
            response["result"]?["isError"]?.GetValue<bool>() == false);
    }

    [Fact]
    public async Task InitializeReturnsWhileConsentIsPendingAndTheFirstToolGetsACalmWait()
    {
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        var home = TestFolder.New();
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        try
        {
            await using var fakeWallet = new NamedPipeServerStream(WalletPaths.McpPipeName(), PipeDirection.InOut,
                1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var options = new McpConnectionOptions(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(1));
            var input = new LinesReader();
            var output = new StringWriter();
            var server = new StdioMcpServer(input, output, options);
            var run = server.RunAsync();
            var fake = FakeWalletAsync(fakeWallet);

            input.Push("{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"timing test\"}}}");
            await EventuallyAsync(() => output.ToString().Contains("\"id\":\"init\"", StringComparison.Ordinal), TimeSpan.FromSeconds(1));
            Assert.DoesNotContain("Waiting for the visitor", output.ToString(), StringComparison.Ordinal);

            input.Push("{\"jsonrpc\":\"2.0\",\"id\":\"first\",\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
            await EventuallyAsync(() => output.ToString().Contains("Waiting for the visitor to answer", StringComparison.Ordinal), TimeSpan.FromSeconds(1));
            Assert.Contains("\"isError\":false", output.ToString(), StringComparison.Ordinal);

            await Task.Delay(300);
            input.Push("{\"jsonrpc\":\"2.0\",\"id\":\"second\",\"method\":\"tools/call\",\"params\":{\"name\":\"status\",\"arguments\":{}}}");
            await EventuallyAsync(() => output.ToString().Contains("\"id\":\"second\"", StringComparison.Ordinal), TimeSpan.FromSeconds(1));
            input.Complete();
            await run;
            await fake;
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
        }
    }

    static async Task FakeWalletAsync(NamedPipeServerStream pipe)
    {
        await pipe.WaitForConnectionAsync();
        var hello = await Framing.ReadAsync(pipe);
        Assert.NotNull(hello);
        await Task.Delay(250);
        await Framing.WriteAsync(pipe, AgentPipeProtocol.Serialize(new JsonObject
        {
            ["type"] = AgentPipeProtocol.Response,
            ["id"] = "hello",
            ["ok"] = true,
            ["fullAccess"] = false,
        }));
        try
        {
            while (true)
            {
                var requestBytes = await Framing.ReadAsync(pipe);
                if (requestBytes is null) return;
                var request = AgentPipeProtocol.Parse(requestBytes);
                if (request is null) return;
                await Framing.WriteAsync(pipe, AgentPipeProtocol.Serialize(new JsonObject
                {
                    ["id"] = request["id"]?.DeepClone(),
                    ["ok"] = true,
                    ["message"] = "fake wallet response",
                }));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    static async Task EventuallyAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new Xunit.Sdk.XunitException("The expected bridge response did not arrive.");
            await Task.Delay(10);
        }
    }

    sealed class LinesReader : TextReader
    {
        readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();

        public void Push(string line) => _lines.Writer.TryWrite(line);
        public void Complete() => _lines.Writer.TryComplete();
        public override async Task<string?> ReadLineAsync()
        {
            try { return await _lines.Reader.ReadAsync(); }
            catch (ChannelClosedException) { return null; }
        }
    }

    static async Task<JsonObject[]> Run(params string[] lines)
    {
        var input = new StringReader(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        var output = new StringWriter();
        var server = new StdioMcpServer(input, output);
        await server.RunAsync();
        return output.ToString().Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    }
}
