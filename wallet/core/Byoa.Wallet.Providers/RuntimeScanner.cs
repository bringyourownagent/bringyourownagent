using System.Text.Json.Nodes;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// Probes the local runtimes: LM Studio, Ollama and the Llama app on their usual ports, plus every OpenAI-compatible
/// endpoint added in the settings. Each probe gets well under a second; a runtime that does not answer in time is simply
/// not running. The scan never throws. The HttpClient comes without automatic redirects, so an endpoint on this
/// computer cannot send the wallet's requests onward to another computer.
/// </summary>
public sealed class RuntimeScanner : IRuntimeScanner
{
    public const string LmStudioEndpoint = "http://127.0.0.1:1234";
    public const string OllamaEndpoint = "http://127.0.0.1:11434";

    /// <summary>How long one probe may take: a runtime that is up answers in a fraction of this.</summary>
    static readonly TimeSpan ProbeTime = TimeSpan.FromMilliseconds(700);

    readonly HttpClient _http;
    readonly Func<IEnumerable<(string Kind, string Endpoint)>>? _added;

    /// <param name="added">The (kind, endpoint) pairs the settings list; null means only the two built-in ports.</param>
    public RuntimeScanner(HttpClient http, Func<IEnumerable<(string Kind, string Endpoint)>>? added = null)
    {
        _http = http;
        _added = added;
    }

    public async Task<IReadOnlyList<RuntimeInfo>> ScanAsync(CancellationToken ct)
    {
        var found = new List<RuntimeInfo>
        {
            await ProbeAsync(SourceKinds.LmStudio, LmStudioEndpoint, ct).ConfigureAwait(false),
            await ProbeAsync(SourceKinds.Ollama, OllamaEndpoint, ct).ConfigureAwait(false),
            // The Llama app is a plain OpenAI-compatible server: a model added from it is an ordinary
            // openai-compatible source, only the window and the messages call it by its name.
            await ProbeAsync(SourceKinds.OpenAiCompatible, LocalRuntimes.LlamaEndpoint, ct).ConfigureAwait(false),
        };
        if (_added is not null)
        {
            foreach (var (kind, endpoint) in _added())
            {
                var address = endpoint?.Trim().TrimEnd('/');
                if (string.IsNullOrEmpty(address) || kind == SourceKinds.OpenRouter) continue;
                if (address.Equals(LmStudioEndpoint, StringComparison.OrdinalIgnoreCase) ||
                    address.Equals(OllamaEndpoint, StringComparison.OrdinalIgnoreCase) ||
                    (kind == SourceKinds.OpenAiCompatible &&
                     address.Equals(LocalRuntimes.LlamaEndpoint, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add(await ProbeAsync(kind, address, ct).ConfigureAwait(false));
            }
        }

        return found;
    }

    async Task<RuntimeInfo> ProbeAsync(string kind, string endpoint, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var probe = kind switch
            {
                SourceKinds.LmStudio => await LmStudioAsync(endpoint, ct).ConfigureAwait(false),
                SourceKinds.Ollama => await OllamaAsync(endpoint, ct).ConfigureAwait(false),
                _ => await OpenAiAsync(endpoint, ct).ConfigureAwait(false),
            };
            return new RuntimeInfo(kind, endpoint, Running: true, probe.Models, watch.ElapsedMilliseconds, probe.Status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A malformed setting, an unexpected JSON shape, or a provider-specific exception means only this
            // runtime is unavailable. It must not take down the scan loop or the settings window.
            return new RuntimeInfo(kind, endpoint, Running: false, Array.Empty<RuntimeModel>(), watch.ElapsedMilliseconds, null);
        }
    }

    /// <summary>LM Studio: the rich v0 list tells which models sit in memory; the plain v1 list is the fallback.</summary>
    async Task<(IReadOnlyList<RuntimeModel> Models, int? Status)> LmStudioAsync(string endpoint, CancellationToken ct)
    {
        var (v0, ok, status) = await GetAsync(endpoint + "/api/v0/models", ct).ConfigureAwait(false);
        if (!ok)
        {
            var (v1, ok1, status1) = await GetAsync(endpoint + "/v1/models", ct).ConfigureAwait(false);
            return (ok1 ? OpenAiList(v1) : [], status1 ?? status);
        }

        if (JsonNode.Parse(v0!) is not JsonObject root || root["data"] is not JsonArray items) return ([], status);
        var models = new List<RuntimeModel>();
        foreach (var item in items)
        {
            var id = ChatCompletions.Str(item?["id"]);
            if (string.IsNullOrEmpty(id)) continue;
            models.Add(new RuntimeModel(id, ChatCompletions.Str(item?["display_name"]), null,
                ChatCompletions.Str(item?["state"]) == "loaded"));
        }

        return (models, status);
    }

    /// <summary>Ollama: /api/tags lists the pulled models with sizes, /api/ps says which are in memory.</summary>
    async Task<(IReadOnlyList<RuntimeModel> Models, int? Status)> OllamaAsync(string endpoint, CancellationToken ct)
    {
        var (tags, ok, status) = await GetAsync(endpoint + "/api/tags", ct).ConfigureAwait(false);
        if (!ok) return ([], status);
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        var (ps, psOk, _) = await GetAsync(endpoint + "/api/ps", ct).ConfigureAwait(false);
        if (psOk && JsonNode.Parse(ps!)?["models"] is JsonArray running)
        {
            foreach (var item in running)
            {
                if (ChatCompletions.Str(item?["name"]) is { } name) loaded.Add(name);
            }
        }

        if (JsonNode.Parse(tags!)?["models"] is not JsonArray items) return ([], status);
        var models = new List<RuntimeModel>();
        foreach (var item in items)
        {
            var id = ChatCompletions.Str(item?["name"]) ?? ChatCompletions.Str(item?["model"]);
            if (string.IsNullOrEmpty(id)) continue;
            models.Add(new RuntimeModel(id, null,
                item?["size"] is JsonValue v && v.TryGetValue<long>(out var s) ? s : null,
                loaded.Contains(id)));
        }

        return (models, status);
    }

    /// <summary>Any other OpenAI-compatible server: the plain model list (nothing about memory).</summary>
    async Task<(IReadOnlyList<RuntimeModel> Models, int? Status)> OpenAiAsync(string endpoint, CancellationToken ct)
    {
        var (json, ok, status) = await GetAsync(endpoint + "/models", ct).ConfigureAwait(false);
        return (ok ? OpenAiList(json) : [], status);
    }

    static IReadOnlyList<RuntimeModel> OpenAiList(string? json)
    {
        if (json is null || JsonNode.Parse(json)?["data"] is not JsonArray items) return [];
        var models = new List<RuntimeModel>();
        foreach (var item in items)
        {
            var id = ChatCompletions.Str(item?["id"]);
            if (string.IsNullOrEmpty(id)) continue;
            models.Add(new RuntimeModel(id, null, null, Loaded: false));
        }

        return models;
    }

    /// <summary>One GET, at most <see cref="ProbeTime"/> long.</summary>
    async Task<(string? Json, bool Ok, int? Status)> GetAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTime);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode) return (null, false, status);
        return (await BoundedHttp.ReadJsonAsync(response, timeout.Token, 512 * 1024).ConfigureAwait(false), true, status);
    }
}
