using System.Text.Json;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>Small read-only scanner for the runtimes used by Add a model.</summary>
sealed class LocalRuntimeScanner : IRuntimeScanner
{
    readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(2) };
    readonly Func<IReadOnlyList<string>> _configuredEndpoints;

    public LocalRuntimeScanner(Func<IReadOnlyList<string>>? configuredEndpoints = null)
    {
        _configuredEndpoints = configuredEndpoints ?? (() => []);
    }

    public async Task<IReadOnlyList<RuntimeInfo>> ScanAsync(CancellationToken ct)
    {
        var endpoints = new List<(string Kind, string Endpoint, Func<CancellationToken, Task<IReadOnlyList<RuntimeModel>>> Read)>
        {
            (SourceKinds.LmStudio, "http://127.0.0.1:1234/v1", token => ReadOpenAiAsync("http://127.0.0.1:1234/v1", token)),
            (SourceKinds.Ollama, "http://127.0.0.1:11434", ReadOllamaAsync),
        };

        foreach (var endpoint in _configuredEndpoints())
        {
            if (string.IsNullOrWhiteSpace(endpoint) || endpoints.Any(x => x.Endpoint.Equals(endpoint, StringComparison.OrdinalIgnoreCase))) continue;
            endpoints.Add((SourceKinds.OpenAiCompatible, endpoint.TrimEnd('/'), token => ReadOpenAiAsync(endpoint.TrimEnd('/'), token)));
        }

        var tasks = endpoints.Select(async e =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var models = await e.Read(timeout.Token).ConfigureAwait(false);
                return new RuntimeInfo(e.Kind, e.Endpoint, true, models);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
            {
                return new RuntimeInfo(e.Kind, e.Endpoint, false, []);
            }
        });
        try
        {
            return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return endpoints.Select(e => new RuntimeInfo(e.Kind, e.Endpoint, false, [])).ToArray();
        }
    }

    async Task<IReadOnlyList<RuntimeModel>> ReadOpenAiAsync(string endpoint, CancellationToken ct)
    {
        using var response = await _probe.GetAsync(endpoint.TrimEnd('/') + "/models", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(model => new RuntimeModel(
            model.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            model.TryGetProperty("name", out var name) ? name.GetString() : null,
            Size(model),
            Loaded(model))).Where(m => m.Id.Length > 0).ToArray();
    }

    async Task<IReadOnlyList<RuntimeModel>> ReadOllamaAsync(CancellationToken ct)
    {
        using var response = await _probe.GetAsync("http://127.0.0.1:11434/api/tags", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
        return models.EnumerateArray().Select(model => new RuntimeModel(
            model.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            model.TryGetProperty("name", out var display) ? display.GetString() : null,
            Size(model),
            model.TryGetProperty("details", out _))).Where(m => m.Id.Length > 0).ToArray();
    }

    static long? Size(JsonElement model)
    {
        if (model.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes)) return bytes;
        if (model.TryGetProperty("size_bytes", out var sizeBytes) && sizeBytes.TryGetInt64(out var value)) return value;
        return null;
    }

    static bool Loaded(JsonElement model) => model.TryGetProperty("loaded", out var loaded) && loaded.ValueKind == JsonValueKind.True;
}
