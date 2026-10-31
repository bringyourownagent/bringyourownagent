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
/// A read does not wait for ever (PROPOSAL-v2 3.6): once the model has said it is finished (<c>finish_reason</c> <c>stop</c> or <c>length</c>) it waits only a
/// short grace for the rest (the usage report, <c>[DONE]</c>) and ends the read with the answer it has. A stream that goes silent for too long before that
/// fails the read when some text has come (a cut answer), and ends it empty when none has (the caller finds no answer and the call fails).
/// </summary>
internal static class ChatStream
{
    public const int MaxChars = 4 * 1024 * 1024;

    /// <summary>How long a stream may send nothing before the read ends (tests shorten it).</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How long the read waits for the end of the stream once the model said it was finished (tests shorten it).</summary>
    public static TimeSpan FinishGrace { get; set; } = TimeSpan.FromSeconds(2);

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
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            silence.CancelAfter(finish is "stop" or "length" ? FinishGrace : IdleTimeout);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(silence.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Nothing more is coming. After finish_reason the answer is whole (only the closing did not arrive); before it, a half-written answer
                // is not an answer: it fails, and a text that stops mid-way is never given to the page as if it were done.
                if (finish is null && text.Length > 0) throw new HttpRequestException("the service went quiet before it finished answering");
                break;
            }

            if (line is null)
            {
                // the connection ended with no [DONE] and no finish_reason: whatever came is a cut answer, not an answer
                if (finish is null && text.Length > 0) throw new HttpRequestException("the service ended the answer before it finished");
                break;
            }

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
            if (ChatCompletions.Content(delta?["content"]) is { Length: > 0 } piece)
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
