using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// The models a service offers behind a key: <c>GET {base}/models</c>, the list every OpenAI-style service has. The picker in "Add a model" shows
/// it so that the visitor chooses instead of typing an id. The key rides in the header and never in an error.
/// </summary>
public static class ServiceModels
{
    public static async Task<IReadOnlyList<string>> ListAsync(HttpClient http, string baseUrl, string key, CancellationToken ct)
    {
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root)
            || !(root.Scheme == Uri.UriSchemeHttps || root.Scheme == Uri.UriSchemeHttp && OpenAiCompatibleSource.IsLoopback(root.Host)))
            throw new ArgumentException("The service address must be a complete https address.", nameof(baseUrl));
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "models"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var text = await BoundedHttp.ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The service answered HTTP " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Parse(text);
    }

    /// <summary>The model ids of a list answer (<c>{"data":[{"id":...}]}</c>), without repeats; Google's <c>models/</c> prefix is dropped.</summary>
    public static IReadOnlyList<string> Parse(string json)
    {
        var ids = new List<string>();
        try
        {
            if (JsonNode.Parse(json)?["data"] is not JsonArray data) return ids;
            foreach (var entry in data)
            {
                if (entry?["id"] is not JsonValue v || !v.TryGetValue<string>(out var id)) continue;
                id = id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id;
                if (id.Length is 0 or > 120 || id.Any(char.IsWhiteSpace) || ids.Contains(id, StringComparer.Ordinal)) continue;
                ids.Add(id);
            }
        }
        catch (JsonException)
        {
            return [];
        }

        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }
}
