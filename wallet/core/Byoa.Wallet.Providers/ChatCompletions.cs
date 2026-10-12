using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// The bits of the OpenAI chat-completions answer the text sources share: the JSON schema that pins an answer to
/// one of the page's choices, and reading that answer back out of a reply.
/// </summary>
static class ChatCompletions
{
    /// <summary>A broken price must not overflow the wallet's money arithmetic.</summary>
    public const decimal MaxCost = 1000m;

    /// <summary>The response_format that pins the answer to exactly one of the choices.</summary>
    public static JsonObject ChoiceSchema(IReadOnlyList<string> choices) => new()
    {
        ["type"] = "json_schema",
        ["json_schema"] = new JsonObject
        {
            ["name"] = "answer",
            ["strict"] = true,
            ["schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["answer"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray(choices.Select(c => JsonValue.Create(c)).ToArray()),
                    },
                },
                ["required"] = new JsonArray("answer"),
                ["additionalProperties"] = false,
            },
        },
    };

    /// <summary>
    /// The choice the model picked, out of its reply message: the JSON in the content, the content itself, or —
    /// when a thinking runtime put the constrained output into its reasoning channel — the JSON there. Text that a
    /// grammar constrained to the schema is not free reasoning, so reading it back shows the page nothing hidden.
    /// </summary>
    public static string? ExtractChoice(JsonNode? message, IReadOnlyList<string> choices)
    {
        var content = Str(message?["content"]);
        var fromContent = FromJson(content, choices) ?? Texts.MatchChoice(content ?? "", choices);
        if (fromContent is not null) return fromContent;
        var reasoning = Str(message?["reasoning_content"]) ?? Str(message?["reasoning"]);
        return FromJson(reasoning, choices) ?? Texts.MatchChoice(reasoning ?? "", choices);
    }

    /// <summary>One of the choices, when the text is (or contains as JSON) the constrained answer object.</summary>
    static string? FromJson(string? text, IReadOnlyList<string> choices)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('"')) return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject answer || answer["answer"] is not JsonValue value) return null;
            return value.TryGetValue<string>(out var s) ? Texts.MatchChoice(s, choices) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static (int In, int Out) Tokens(JsonNode? usage) =>
        (Int(usage?["prompt_tokens"]), Int(usage?["completion_tokens"]));

    /// <summary>The cost a server reports for the call (OpenRouter's <c>usage.cost</c>), or null.</summary>
    public static decimal? Cost(JsonNode? usage)
    {
        if (usage?["cost"] is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d is >= 0 and <= MaxCost ? d : null;
        if (v.TryGetValue<double>(out var f) && double.IsFinite(f) && f is >= 0 and <= (double)MaxCost)
            return (decimal)f;
        if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= MaxCost) return p;
        return null;
    }

    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
