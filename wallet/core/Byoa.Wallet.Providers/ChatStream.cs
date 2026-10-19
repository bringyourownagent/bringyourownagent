using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>What a streamed chat answer came to: the visible text as the model wrote it, its hidden reasoning, the usage report and why it stopped.</summary>
internal sealed record StreamResult(string Text, string Reasoning, JsonNode? Usage, string? Finish);

/// <summary>
/// Reads a chat answer that arrives as server-sent events (<c>data: {...}</c> lines, <c>[DONE]</c> at the end) and hands the text over as it comes, with
/// the model's reasoning left out. A service that reports an error in the stream ends the read. The size of the whole answer is bounded.
/// </summary>
internal static class ChatStream
{
    public const int MaxChars = 4 * 1024 * 1024;

    public static async Task<StreamResult> ReadAsync(HttpResponseMessage response, Action<string>? onText, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192);
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var filter = new ThinkFilter();
        JsonNode? usage = null;
        string? finish = null;
        long seen = 0;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            seen += line.Length;
            if (seen > MaxChars) throw new HttpRequestException("the answer is longer than the wallet takes");
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;                 // comments (": PROCESSING") and blank lines
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;
            JsonNode? json;
            try
            {
                json = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;                                                                       // a broken piece is skipped, as a browser's reader would
            }

            if (json?["error"] is not null) throw new HttpRequestException("the service reported an error while answering");
            if (json?["usage"] is JsonObject report) usage = report;
            var choice = ChatCompletions.First(json);
            if (ChatCompletions.Str(choice?["finish_reason"]) is { } why) finish = why;
            var delta = choice?["delta"];
            if ((ChatCompletions.Str(delta?["reasoning_content"]) ?? ChatCompletions.Str(delta?["reasoning"])) is { Length: > 0 } thought)
                reasoning.Append(thought);
            if (ChatCompletions.Str(delta?["content"]) is { Length: > 0 } piece)
            {
                text.Append(piece);
                var visible = filter.Push(piece);
                if (visible.Length > 0) onText?.Invoke(visible);
            }
        }

        var rest = filter.Flush();
        if (rest.Length > 0) onText?.Invoke(rest);
        return new StreamResult(text.ToString(), reasoning.ToString(), usage, finish);
    }
}
