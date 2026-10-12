using System.Text.Json;
using System.Text.Json.Nodes;

namespace Byoa.Wallet.Protocol;

/// <summary>The private, framed protocol between byoa-mcp.exe and the running wallet.</summary>
public static class AgentPipeProtocol
{
    public const string Hello = "hello";
    public const string Request = "request";
    public const string Response = "response";

    public static byte[] Serialize(JsonObject message) => JsonSerializer.SerializeToUtf8Bytes(message);

    public static JsonObject? Parse(ReadOnlySpan<byte> bytes)
    {
        try { return JsonNode.Parse(bytes) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };
}
