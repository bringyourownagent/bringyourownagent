using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Byoa.Wallet.Guard;

namespace Byoa.Wallet.Providers;

/// <summary>
/// Jev (TypeSafe), a decision model, through OpenRouter's Decisions API, with the visitor's own OpenRouter key. It writes no text: each statement
/// comes back as the chance that it is true. It costs about 4 cents for a thousand requests; the key and the daily limit are the visitor's.
/// The Decisions API is marked alpha by OpenRouter, so every answer is checked before it is believed.
/// </summary>
public sealed class JevDecisionClient(HttpClient http, Func<string?> key, Uri? endpoint = null, string? model = null) : IDecisionClient
{
    public const string DefaultModel = "typesafe/jev-1.14";

    public static readonly Uri DefaultEndpoint = new("https://openrouter.ai/api/alpha/decisions");

    /// <summary>The price of an input token when a response does not say what it cost: 0.042 dollars a million, rounded up.</summary>
    const decimal InputTokenPrice = 0.00000005m;

    /// <summary>The longest the visitor's site waits for the guard.</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(25);

    readonly Uri _endpoint = endpoint ?? DefaultEndpoint;
    readonly string _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    public bool HasKey => !string.IsNullOrWhiteSpace(key());

    public async Task<DecisionAnswer> AskAsync(JsonObject state, IReadOnlyDictionary<string, string> statements, CancellationToken ct)
    {
        var apiKey = key();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new DecisionException("the guard needs your OpenRouter key");

        var questions = new JsonObject();
        foreach (var (name, text) in statements) questions[name] = new JsonObject { ["type"] = "noul", ["instructions"] = text };
        var body = new JsonObject { ["model"] = _model, ["state"] = state.DeepClone(), ["questions"] = questions };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new DecisionException("OpenRouter could not be reached", 0, e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DecisionException("the guard did not answer in time");
        }

        using (response)
        {
            string text;
            try
            {
                text = await BoundedHttp.ReadJsonAsync(response, limit.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw new DecisionException("the guard's answer was cut off", 0, e);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DecisionException("the guard did not answer in time");
            }

            if (!response.IsSuccessStatusCode) throw new DecisionException(Refusal((int)response.StatusCode, text));
            return Parse(text, statements.Keys);
        }
    }

    static DecisionAnswer Parse(string text, IEnumerable<string> names)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new DecisionException("the guard's answer was not understood", 0, e);
        }

        var cost = CostOf(root?["usage"]);
        var yes = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var answer = root?["answers"]?[name];
            if (answer?["type"]?.GetValue<string>() != "noul" || answer["noul"] is not JsonValue v || !v.TryGetValue<double>(out var p)
                || double.IsNaN(p) || p is < 0 or > 1)
                throw new DecisionException("the guard did not answer \"" + name + "\" in a form the wallet can use", cost);
            yes[name] = p;
        }

        return new DecisionAnswer(yes, cost);
    }

    /// <summary>What the call cost: the response's own figure, or the input tokens at Jev's price.</summary>
    static decimal CostOf(JsonNode? usage)
    {
        if (usage?["cost"] is JsonValue c && c.TryGetValue<decimal>(out var cost) && cost >= 0) return cost;
        return usage?["input_tokens"] is JsonValue t && t.TryGetValue<long>(out var tokens) && tokens > 0
            ? tokens * InputTokenPrice
            : 0.0001m;
    }

    static string Refusal(int status, string body)
    {
        string? message = null;
        try
        {
            message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            // not JSON: only the status says anything
        }

        var why = status switch
        {
            401 or 403 => "OpenRouter did not accept your key",
            402 => "your OpenRouter account has no credit",
            429 => "OpenRouter asked to slow down",
            _ => "OpenRouter answered " + status.ToString(CultureInfo.InvariantCulture),
        };
        if (message is { Length: > 0 })
        {
            // a long run of letters and digits in a refusal is a key: it never reaches a window or a log
            var clean = System.Text.RegularExpressions.Regex.Replace(new string(message.Where(ch => !char.IsControl(ch)).ToArray()), @"[A-Za-z0-9_.\-]{24,}", "…");
            why += ": " + (clean.Length > 120 ? clean[..120] + "…" : clean);
        }

        return why;
    }
}
