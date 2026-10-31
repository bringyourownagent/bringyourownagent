using System.Globalization;
using System.Text.Json.Nodes;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// OpenRouter's public model list (<c>api/v1/models</c>, no key needed): the catalog the pickers search and the prices come from. It is read at most once
/// a day, kept on disk so that the next start has it at once, and never throws: without a list the wallet still works, with flat price estimates and
/// a picker that says so.
/// </summary>
public sealed class OpenRouterCatalog : IModelCatalog
{
    const string Endpoint = "api/v1/models";
    const int MaxListBytes = 12 * 1024 * 1024;
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    readonly HttpClient _http;
    readonly string? _cachePath;
    readonly Uri _baseUrl;
    readonly object _gate = new();
    volatile Snapshot _snapshot = new([], new Dictionary<string, CatalogModel>(StringComparer.Ordinal));
    DateTime _checkedAt;

    sealed record Snapshot(IReadOnlyList<CatalogModel> All, IReadOnlyDictionary<string, CatalogModel> ByCanonical);

    /// <param name="cachePath">Where the last list is kept; null keeps nothing (tests).</param>
    public OpenRouterCatalog(HttpClient http, string? cachePath, Uri? baseUrl = null)
    {
        _http = http;
        _cachePath = cachePath;
        _baseUrl = baseUrl ?? new Uri("https://openrouter.ai/");
        LoadCache();
    }

    public bool Loaded => _snapshot.All.Count > 0;

    /// <summary>When the list in memory was last fetched or read from the disk copy, UTC; null if never.</summary>
    public DateTime? ListedAt { get; private set; }

    public CatalogModel? Find(string? name)
    {
        var canonical = ModelNames.Canon(name);
        if (canonical.Length == 0) return null;
        return _snapshot.ByCanonical.TryGetValue(canonical, out var exact) ? exact : null;
    }

    public IReadOnlyList<CatalogModel> Search(string? query, CatalogFilter filter = CatalogFilter.All, int limit = 40)
    {
        var words = (query ?? "").ToLowerInvariant().Split([' ', '\t', '/'], StringSplitOptions.RemoveEmptyEntries);
        // The primary-role rule (PROPOSAL-v2 3.3): a text picker never offers a model that draws pictures. The catalog keeps them,
        // so `Find` still knows their price and their modalities when a visitor adds one by name.
        IEnumerable<CatalogModel> found = _snapshot.All.Where(m => m.OffersText);
        if (filter == CatalogFilter.Free) found = found.Where(m => m.Free);
        if (filter == CatalogFilter.Cheap) found = found.Where(m => m.TypicalAnswerCost is { } cost && cost <= 0.002m);
        if (words.Length > 0)
        {
            found = found.Where(m =>
            {
                var haystack = (m.Id + " " + m.Name).ToLowerInvariant();
                return words.All(w => haystack.Contains(w, StringComparison.Ordinal));
            });
            var first = words[0];
            return found.OrderBy(m => m.Id.StartsWith(first, StringComparison.OrdinalIgnoreCase) || m.Id.Contains("/" + first, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(m => m.Created).Take(Math.Max(1, limit)).ToList();
        }

        return found.OrderByDescending(m => m.Created).Take(Math.Max(1, limit)).ToList();
    }

    /// <summary>Reads the list from the network if the copy is older than a day (or <paramref name="force"/>). Returns whether a new list arrived.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct, bool force = false)
    {
        lock (_gate)
        {
            if (!force && Loaded && DateTime.UtcNow - (ListedAt ?? DateTime.MinValue) < MaxAge) return false;
            if (!force && DateTime.UtcNow - _checkedAt < TimeSpan.FromMinutes(10)) return false;     // a failed try is not repeated at once
            _checkedAt = DateTime.UtcNow;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await _http.GetAsync(new Uri(_baseUrl, Endpoint), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await BoundedHttp.ReadJsonAsync(response, timeout.Token, MaxListBytes).ConfigureAwait(false);
            var parsed = Parse(json);
            if (parsed.Count == 0) return false;
            Replace(parsed, DateTime.UtcNow);
            SaveCache(json);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;                       // no list today; what the wallet had stays
        }
    }

    /// <summary>The list as models: every model is kept (one that draws pictures is a model too, only never a text one), ids that look like
    /// ids, prices read as dollars per token, and what the list says goes in and out.</summary>
    public static IReadOnlyList<CatalogModel> Parse(string json)
    {
        var models = new List<CatalogModel>();
        try
        {
            if (JsonNode.Parse(json)?["data"] is not JsonArray data) return models;
            foreach (var entry in data)
            {
                if (entry?["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id)) continue;
                if (id.Length is 0 or > 120 || id.Any(char.IsWhiteSpace)) continue;
                var name = Text(entry["name"]) ?? id;
                var context = entry["context_length"] is JsonValue c && c.TryGetValue<double>(out var cd) && double.IsFinite(cd) && cd is >= 0 and <= int.MaxValue ? (int)cd : 0;
                var created = entry["created"] is JsonValue t && t.TryGetValue<double>(out var td) && double.IsFinite(td) ? (long)td : 0;
                models.Add(new CatalogModel(id, name.Length > 160 ? name[..160] : name, context,
                    Price(entry["pricing"]?["prompt"]), Price(entry["pricing"]?["completion"]), created,
                    Words(entry["architecture"]?["input_modalities"]), Words(entry["architecture"]?["output_modalities"])));
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }

        return models;
    }

    /// <summary>
    /// The modality words the list gives, as it spells them (<c>text</c>, <c>image</c>, <c>audio</c>, but also <c>embeddings</c>...): kept whole, so that a
    /// model that writes no text is known not to, whatever it writes instead. Null when the list says nothing.
    /// </summary>
    static IReadOnlyList<string>? Words(JsonNode? node)
    {
        if (node is not JsonArray list) return null;
        var words = new List<string>();
        foreach (var item in list)
        {
            if (words.Count >= 8) break;
            if (item is not JsonValue v || !v.TryGetValue<string>(out var word)) continue;
            word = word.Trim().ToLowerInvariant();
            if (word.Length is 0 or > 24 || !word.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || words.Contains(word)) continue;
            words.Add(word);
        }

        return words.Count == 0 ? null : words;
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    /// <summary>Dollars per token from the list's string ("0.0000015"); a price above one dollar a token is not a price.</summary>
    static decimal? Price(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed is >= 0 and <= 1m ? parsed : null;
        if (value.TryGetValue<double>(out var d) && double.IsFinite(d) && d is >= 0 and <= 1d) return (decimal)d;
        return null;
    }

    void Replace(IReadOnlyList<CatalogModel> models, DateTime at)
    {
        var map = new Dictionary<string, CatalogModel>(StringComparer.Ordinal);
        foreach (var model in models.OrderBy(m => m.Id.Contains(':') ? 1 : 0))      // the plain id before its ":free" variant
            map.TryAdd(model.Canonical, model);
        _snapshot = new Snapshot(models, map);
        ListedAt = at;
    }

    void LoadCache()
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return;
        try
        {
            var parsed = Parse(File.ReadAllText(_cachePath));
            if (parsed.Count > 0) Replace(parsed, File.GetLastWriteTimeUtc(_cachePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an unreadable copy is no copy
        }
    }

    void SaveCache(string json)
    {
        if (_cachePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the list stays in memory; the next start fetches it again
        }
    }
}
