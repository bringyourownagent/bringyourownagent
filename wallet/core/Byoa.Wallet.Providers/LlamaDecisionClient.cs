using System.Text;
using System.Text.Json.Nodes;
using Byoa.Wallet.Guard;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// Kev, a small decision model, as the guard's judge, running in the Llama app on this computer. It speaks the same language as Jev: the state and the
/// statements go to the app's <c>/v1/systemone</c>, and each statement comes back as the chance that it is true. Nothing leaves the computer, there is
/// no key and the answer costs nothing. The app has the model by a name of its own choosing (<c>ggml-org/Kev-4B-GGUF:Q4_K_M</c>, or another quant), so the
/// wallet asks the app's model list which one is Kev. Like every local model it has a small context (4,096 tokens as the app loads it), so a long request is
/// read in windows exactly as a text model's is. It never starts a model the visitor said it must not: a Kev that is not loaded is "unavailable" with a plain
/// reason, and the guard's stop-when-unavailable setting decides what follows.
/// </summary>
/// <param name="neverStartModels">The visitor's "never start a model for me" setting: when it is on, a Kev that is not in memory is not asked.</param>
/// <param name="apiBase">The Llama app's OpenAI-style address with a trailing slash; null means the app's usual port on this computer.</param>
public sealed class LlamaDecisionClient(HttpClient http, int maxInputChars, Func<bool> neverStartModels, Uri? apiBase = null) : IDecisionClient
{
    /// <summary>What an entry of the app's model list must contain to be Kev.</summary>
    public const string ModelMark = "kev";

    const string App = "the Llama app";

    readonly Uri _base = apiBase ?? new Uri(LocalRuntimes.LlamaEndpoint + "/");

    public bool HasKey => true;                              // a model on this computer needs no key

    public Task<DecisionAnswer> AskAsync(JsonObject state, IReadOnlyDictionary<string, string> statements, CancellationToken ct) =>
        ModelDecisionClient.AskInWindows(state, statements, maxInputChars, (one, _) => AskOnce(one, statements, ct));

    async Task<DecisionAnswer> AskOnce(JsonObject state, IReadOnlyDictionary<string, string> statements, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ModelDecisionClient.Timeout);          // the first call may have to wait for the model to be read into memory
        try
        {
            var model = await FindKevAsync(limit.Token).ConfigureAwait(false);
            var questions = new JsonObject();
            foreach (var (name, text) in statements) questions[name] = new JsonObject { ["type"] = "noul", ["instructions"] = text };
            var body = new JsonObject { ["model"] = model, ["state"] = state.DeepClone(), ["questions"] = questions };

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "systemone"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var answer = await BoundedHttp.ReadJsonAsync(response, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new DecisionException(JevDecisionClient.Refusal(App, (int)response.StatusCode, answer));
            return JevDecisionClient.Parse(answer, statements.Keys, free: true);
        }
        catch (HttpRequestException e)
        {
            throw new DecisionException(App + " could not be reached: is it running?", 0, e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DecisionException("Kev did not answer in time");
        }
    }

    /// <summary>The name the app gives Kev, from its model list (a plain list request, which loads nothing); loaded one first when there are several.</summary>
    async Task<string> FindKevAsync(CancellationToken ct)
    {
        JsonNode? list;
        using (var response = await http.GetAsync(new Uri(_base, "models"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) throw new DecisionException(JevDecisionClient.Refusal(App, (int)response.StatusCode, ""));
            try
            {
                list = JsonNode.Parse(await BoundedHttp.ReadJsonAsync(response, ct, 512 * 1024).ConfigureAwait(false));
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new DecisionException(App + " did not say which models it has", 0, e);
            }
        }

        var kevs = (list?["data"] as JsonArray ?? [])
            .Where(m => ChatCompletions.Str(m?["id"]) is { } id && id.Contains(ModelMark, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => LoadState.OfOpenAiEntry(m) == true)
            .ToList();
        if (kevs.Count == 0)
            throw new DecisionException("Kev is not among the models of your Llama app: add ggml-org/Kev-4B-GGUF there");
        var kev = kevs[0]!;
        if (LoadState.OfOpenAiEntry(kev) == false && neverStartModels())
            throw new DecisionException("Kev is not loaded in your Llama app, and you chose that the wallet never starts a model");
        return ChatCompletions.Str(kev["id"])!;
    }
}
