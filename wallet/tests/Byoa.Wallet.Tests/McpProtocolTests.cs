using System.Text.Json.Nodes;
using Byoa.Wallet.Mcp;

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
