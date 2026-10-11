using System.Text.Json;
using System.Text.Json.Nodes;

namespace Byoa.Wallet.Protocol;

/// <summary>
/// The messages between the extension, the native host and the wallet (CONTRACT.md §4):
/// <c>{ "id": n, "type": "request"|"call"|"release", "origin": "...", "userActivation": true|false, "payload": {...} }</c>
/// answered by <c>{ "id": n, "result": ... }</c>. Two more types serve only the extension's own popup:
/// <c>status</c> (is the wallet running?) and <c>open</c> (open the wallet's window).
/// </summary>
public static class Wire
{
    public const string Request = "request";
    public const string Call = "call";
    public const string Release = "release";
    public const string Status = "status";
    public const string Open = "open";

    /// <summary>
    /// A <c>bringyourownagent://</c> link handed to the running wallet by a second <c>Byoa.Wallet.exe --url</c>
    /// (the direct transport). Never from the browser: the native host does not forward it.
    /// </summary>
    public const string Link = "link";

    /// <summary>The types the native host forwards from the browser; anything else gets the fallback there.</summary>
    public static bool FromBrowser(string? type) => type is Request or Call or Release or Status or Open;

    /// <summary>The answer when nothing better can be given: null for a request, <c>{ ok: false }</c> for a call.</summary>
    public static JsonNode? Fallback(string? type) => type == Call ? new JsonObject { ["ok"] = false } : null;

    /// <summary>The response frame for a message id.</summary>
    public static byte[] Response(JsonNode? id, JsonNode? result)
    {
        var response = new JsonObject { ["id"] = id?.DeepClone(), ["result"] = result?.DeepClone() };
        return JsonSerializer.SerializeToUtf8Bytes(response);
    }

    /// <summary>Reads the id and the type of a message without trusting anything else in it.</summary>
    public static bool TryPeek(ReadOnlySpan<byte> json, out JsonNode? id, out string? type)
    {
        id = null;
        type = null;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj) return false;
            id = obj["id"]?.DeepClone();
            type = obj["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>One message from the extension, as the wallet reads it.</summary>
public sealed record WireMessage(JsonNode? Id, string Type, string Origin, bool UserActivation, JsonObject Payload)
{
    /// <summary>Parses a message. Returns null when it is not a message of the protocol.</summary>
    public static WireMessage? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return null;
            var type = obj["type"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
            if (type is null) return null;
            var origin = obj["origin"] is JsonValue o && o.TryGetValue<string>(out var os) ? os : "";
            var activation = obj["userActivation"] is JsonValue a && a.TryGetValue<bool>(out var b) && b;
            var payload = obj["payload"] as JsonObject ?? new JsonObject();
            payload = (JsonObject)payload.DeepClone();
            return new WireMessage(obj["id"]?.DeepClone(), type, origin, activation, payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
