using System.Text.Json.Nodes;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// Whether a model is in a local runtime's memory right now, asked with plain list requests that never load a model: the Llama app
/// lists every model with a status (<c>loaded</c>, <c>unloaded</c>, <c>loading</c>, <c>downloading</c>), LM Studio's <c>/api/v0/models</c> gives
/// each one a state, and Ollama's <c>/api/ps</c> lists what is in memory. A server that says nothing about memory (a plain OpenAI list) answers null:
/// the wallet cannot tell, so it does not hold the model back.
/// </summary>
public static class LoadState
{
    /// <summary>How long one question may take; a runtime that is up answers in a fraction of this.</summary>
    static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(700);

    /// <summary>The status of an entry of the Llama app's model list: true loaded or loading, false any other status, null when the entry has none.</summary>
    public static bool? OfOpenAiEntry(JsonNode? entry)
    {
        var status = entry?["status"];
        var value = status is JsonValue plain && plain.TryGetValue<string>(out var word) ? word : ChatCompletions.Str(status?["value"]);
        if (string.IsNullOrEmpty(value)) return null;
        return value is "loaded" or "loading";
    }

    public static async Task<bool?> QueryAsync(HttpClient http, string kind, Uri apiBase, string model, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Wait);
            if (kind.Equals(SourceKinds.Ollama, StringComparison.OrdinalIgnoreCase))
            {
                var ps = await GetAsync(http, new Uri(apiBase, "/api/ps"), limit.Token).ConfigureAwait(false);
                if (ps?["models"] is not JsonArray running) return null;
                return running.Any(m => ChatCompletions.Str(m?["name"]) == model || ChatCompletions.Str(m?["model"]) == model);
            }

            if (kind.Equals(SourceKinds.LmStudio, StringComparison.OrdinalIgnoreCase))
            {
                var v0 = await GetAsync(http, new Uri(apiBase, "/api/v0/models"), limit.Token).ConfigureAwait(false);
                if (v0?["data"] is JsonArray all)
                {
                    var mine = all.FirstOrDefault(m => ChatCompletions.Str(m?["id"]) == model);
                    return mine is null ? null : ChatCompletions.Str(mine["state"]) == "loaded";
                }
            }

            var list = await GetAsync(http, new Uri(apiBase, "models"), limit.Token).ConfigureAwait(false);
            if (list?["data"] is not JsonArray items) return null;
            return OfOpenAiEntry(items.FirstOrDefault(m => ChatCompletions.Str(m?["id"]) == model));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;                                           // it did not answer: nothing is known, nothing is held back
        }
    }

    static async Task<JsonNode?> GetAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await BoundedHttp.ReadJsonAsync(response, ct, 512 * 1024).ConfigureAwait(false) ?? "null");
    }
}
