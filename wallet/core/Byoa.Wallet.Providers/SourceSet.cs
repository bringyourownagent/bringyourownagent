using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;
using System.Net;
using System.Net.Sockets;
using System.Collections.ObjectModel;

namespace Byoa.Wallet.Providers;

/// <summary>An added text source setting that the provider set could not construct.</summary>
public sealed record InvalidTextSource(string Id, string Label, string Message);

/// <summary>The models found in the visitor's model folders, rescanned at most once a minute (or on demand).</summary>
public sealed class ModelCatalog
{
    readonly Func<IEnumerable<string>> _folders;
    readonly object _gate = new();
    List<FoundModel> _found = [];
    DateTime _scanned = DateTime.MinValue;

    public ModelCatalog(Func<IEnumerable<string>> folders) => _folders = folders;

    public IReadOnlyList<FoundModel> Found
    {
        get
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _scanned > TimeSpan.FromMinutes(1)) Rescan();
                return _found;
            }
        }
    }

    public void Rescan()
    {
        lock (_gate)
        {
            _found = ModelScanner.Scan(_folders());
            _scanned = DateTime.UtcNow;
        }
    }
}

/// <summary>
/// Builds the wallet's sources from the settings and the secret store: the three speech sources of 0.1 plus the
/// CPU Whisper fallback, and one text source per added source in the settings (a model in LM Studio or Ollama, an
/// OpenAI-compatible server, an OpenRouter model). The text sources live in one stable list — the engine holds it
/// once and never sees a half-built set — and it rebuilds itself whenever the settings change. A background scan
/// keeps the "is the runtime running" states fresh.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SourceSet : IDisposable
{
    readonly SettingsStore _store;
    readonly SecretStore _secrets;
    readonly CancellationTokenSource _scans = new();
    readonly SourceList _text = new();
    int _rebuilding;
    int _rebuildPending;
    Task? _scanLoop;
    readonly object _sourceGate = new();
    readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveEndpoint;
    Dictionary<string, ITextSource> _lastGood = new(StringComparer.Ordinal);
    IReadOnlyList<InvalidTextSource> _invalid = [];

    public SourceSet(SettingsStore store, SecretStore secrets, HttpClient http,
        CpuFeatureSet? cpuFeatures = null, Func<VulkanProbeResult>? vulkanProbe = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? endpointResolver = null)
    {
        _store = store;
        _secrets = secrets;
        _resolveEndpoint = endpointResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        var features = cpuFeatures ?? CpuFeatures.Current;
        // Text calls go through a client that never follows redirects: an endpoint on this computer must not send
        // the wallet's requests (and the visitor's API key) onward to another computer.
        var textHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        textHandler.ConnectCallback = ConnectTextAsync;
        TextHttp = new HttpClient(textHandler)
        {
            Timeout = TimeSpan.FromSeconds(150),
            MaxResponseContentBufferSize = BoundedHttp.MaxJsonBytes,
        };

        // A new wallet looks in its own models folder and the usual model folders on this computer.
        if (store.Read(s => s.ModelFolders.Count == 0))
        {
            var defaults = ModelScanner.DefaultFolders();
            defaults.Add(Path.Combine(WalletPaths.Home, "models"));
            store.Update(s => s.ModelFolders.AddRange(defaults));
        }

        Catalog = new ModelCatalog(() => store.Read(s => s.ModelFolders.ToList()));
        // The Whisper loader's order is process-global. Decide it before any source can create a native factory.
        WhisperSource.ConfigureProcessRuntime(features, cpuSourceEnabled: true, probe: vulkanProbe);
        GigaAm = new GigaAmSource(() => Catalog.Found.FirstOrDefault(m => m.Kind == "gigaam")?.Path, http);
        Whisper = new WhisperSource(() => store.Read(s => s.WhisperModel), () => Catalog.Found, http, features);
        WhisperCpu = new WhisperCpuSource(() => Catalog.Found, http, features, processGpuOverride: WhisperSource.ProcessUsesGpu);
        OpenRouter = new OpenRouterSource(http, () => secrets.Get(OpenRouterSource.SecretName), () => secrets.Has(OpenRouterSource.SecretName));
        All = [GigaAm, Whisper, WhisperCpu, OpenRouter];
        Scanner = new RuntimeScanner(TextHttp, AddedEndpoints);
        // Older settings used an Ollama id without /v1. Canonicalise those ids once before route repair.
        _store.UpdateIfChanged(NormalizeAddedSourceIds);
        // The registry normally adds the route in the same settings update as the source. For older settings,
        // repair the route once before subscribing to Changed; a rebuild must never write settings.json again.
        _store.UpdateIfChanged(Router.EnsureTextChainFromSettings);
        RebuildText();
        store.Changed += RebuildText;
        _scanLoop = Task.Run(() => ScanLoop(_scans.Token));
    }

    public ModelCatalog Catalog { get; }
    public GigaAmSource GigaAm { get; }
    public WhisperSource Whisper { get; }
    public WhisperCpuSource WhisperCpu { get; }
    public OpenRouterSource OpenRouter { get; }
    public IReadOnlyList<ISpeechSource> All { get; }

    /// <summary>The HttpClient every text source and the runtime scanner use: no redirects, patient but bounded.</summary>
    public HttpClient TextHttp { get; }

    /// <summary>The runtime scanner; it watches LM Studio, Ollama and every added endpoint.</summary>
    public RuntimeScanner Scanner { get; }

    /// <summary>Re-resolves agent-added named endpoints at use time and rejects unsafe DNS drift.</summary>
    async ValueTask<Stream> ConnectTextAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var setting = _store.Snapshot().Sources.FirstOrDefault(source =>
            source.AddedByAgent && source.EndpointConfirmed &&
            Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var uri) &&
            !IPAddress.TryParse(uri.Host, out _) &&
            string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase) &&
            (uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port) == port);

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await _resolveEndpoint(host, ct).WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            MarkEndpointUnavailable(host, port, "the endpoint name could not be resolved safely");
            throw new HttpRequestException("The endpoint name could not be resolved safely.", e);
        }

        var candidates = setting is null
            ? addresses
            : addresses.Where(address => EndpointPolicy.IsAllowedAtUse(address, setting.ConfirmedAddressClass,
                setting.ConfirmedAddresses)).ToArray();
        if (candidates.Length == 0)
        {
            var reason = setting is null ? "the endpoint has no usable address" :
                "the endpoint name resolved to an address outside the visitor's confirmed network";
            MarkEndpointUnavailable(host, port, reason);
            throw new HttpRequestException("The endpoint is unavailable: " + reason + ".");
        }

        Exception? last = null;
        foreach (var address in candidates.Distinct())
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient(address.AddressFamily);
                await client.ConnectAsync(address, port, ct).ConfigureAwait(false);
                MarkEndpointAvailable(host, port);
                return client.GetStream();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                last = e;
                client?.Dispose();
            }
        }

        MarkEndpointUnavailable(host, port, "the endpoint did not accept a connection");
        throw new HttpRequestException("The endpoint did not accept a connection.", last);
    }

    void MarkEndpointUnavailable(string host, int port, string reason)
    {
        foreach (var source in _text.OfType<OpenAiCompatibleSource>())
            if (EndpointMatches(source.Endpoint, host, port)) source.MarkUnavailable(reason);
    }

    void MarkEndpointAvailable(string host, int port)
    {
        foreach (var source in _text.OfType<OpenAiCompatibleSource>())
            if (EndpointMatches(source.Endpoint, host, port)) source.MarkAvailable();
    }

    static bool EndpointMatches(string endpoint, string host, int port)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        var endpointPort = uri.IsDefaultPort ? uri.Scheme == "https" ? 443 : 80 : uri.Port;
        return endpointPort == port && string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The text sources as the settings list them. The engine keeps this list for its whole life.</summary>
    public IReadOnlyList<ITextSource> Text => _text;

    /// <summary>The text source with this id ("lmstudio:qwen/qwen3.5-4b"), or null.</summary>
    public ITextSource? FindText(string id) => _text.FirstOrDefault(s => s.Id == id);

    /// <summary>Added source settings that could not be materialised; the Sources page can show the readable reason.</summary>
    public IReadOnlyList<InvalidTextSource> InvalidTextSources
    {
        get { lock (_sourceGate) return new ReadOnlyCollection<InvalidTextSource>(_invalid.ToList()); }
    }

    // ------------------------------------------------------------------------------------------------
    // the text sources follow the settings

    void RebuildText()
    {
        Interlocked.Exchange(ref _rebuildPending, 1);
        if (Interlocked.CompareExchange(ref _rebuilding, 1, 0) == 1) return;
        try
        {
            while (Interlocked.Exchange(ref _rebuildPending, 0) == 1)
            {
                var settings = _store.Snapshot();
                var list = new List<ITextSource>();
                var invalid = new List<InvalidTextSource>();
                foreach (var setting in settings.Sources)
                {
                    // Keep disabled sources materialised so the registry and the Skills page can show them and
                    // Router.PlanText can explain "turned off". The route, not this list, owns the enabled check.
                    if (setting.Skills.Count > 0 && !setting.Skills.Contains(Vocabulary.Generate)) continue;
                    try
                    {
                        var source = CreateTextSource(setting);
                        if (source is null)
                        {
                            invalid.Add(new InvalidTextSource(setting.Id, setting.Label ?? setting.Model ?? setting.Id,
                                "This source setting is incomplete."));
                            continue;
                        }

                        if (list.Any(s => s.Id == source.Id))
                        {
                            invalid.Add(new InvalidTextSource(setting.Id, setting.Label ?? setting.Model ?? setting.Id,
                                "This source has the same id as another source."));
                            continue;
                        }

                        list.Add(source);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        invalid.Add(new InvalidTextSource(setting.Id, setting.Label ?? setting.Model ?? setting.Id,
                            e.Message));
                        lock (_sourceGate)
                        {
                            if (_lastGood.TryGetValue(setting.Id, out var old) && !list.Any(s => s.Id == old.Id)) list.Add(old);
                        }
                    }
                }

                _text.Replace(list);
                lock (_sourceGate)
                {
                    _lastGood = list.ToDictionary(s => s.Id, StringComparer.Ordinal);
                    _invalid = invalid;
                }

            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A malformed setting, serializer problem, or a provider constructor must never escape SettingsStore.Update.
            lock (_sourceGate)
                _invalid = [new InvalidTextSource("settings", "Text sources", e.Message)];
        }
        finally
        {
            Interlocked.Exchange(ref _rebuilding, 0);
            if (Volatile.Read(ref _rebuildPending) == 1) RebuildText();
        }
    }

    /// <summary>Materialises one text source with the same provider construction used by the live list.</summary>
    public ITextSource? CreateTextSource(SourceSetting setting) => CreateTextSource(setting, null);

    /// <summary>
    /// Materialises a draft source for the wallet's visitor-initiated test. The optional key is held only by the
    /// temporary provider instance; saving a source still goes through the registry and SecretStore.
    /// </summary>
    public ITextSource? CreateTextSource(SourceSetting setting, string? transientApiKey)
    {
        if (setting.Kind is null || string.IsNullOrWhiteSpace(setting.Model)) return null;
        var kind = setting.Kind.Trim().ToLowerInvariant();
        var endpoint = kind == SourceKinds.OpenRouter
            ? "https://openrouter.ai/api/v1"
            : setting.Endpoint?.Trim() is { Length: > 0 } configured
                ? configured
                : OpenAiCompatibleSource.DefaultEndpoint(kind);
        var endpointPolicy = EndpointPolicy.Check(endpoint, agentSupplied: false);
        if (!endpointPolicy.Allowed) throw new ArgumentException(endpointPolicy.Message);
        var testKey = string.IsNullOrWhiteSpace(transientApiKey) ? null : transientApiKey.Trim();
        ITextSource? source = kind switch
        {
            SourceKinds.LmStudio or SourceKinds.Ollama
                => new OpenAiCompatibleSource(setting.Kind, setting.Model.Trim(), setting.Label, setting.Endpoint, TextHttp),
            SourceKinds.OpenAiCompatible
                when !string.IsNullOrWhiteSpace(setting.Endpoint)
                => new OpenAiCompatibleSource(setting.Kind, setting.Model.Trim(), setting.Label, setting.Endpoint, TextHttp),
            SourceKinds.OpenRouter
                => new OpenRouterTextSource(setting.Model.Trim(), setting.Label, setting.SecretRef, TextHttp,
                    () => testKey ?? _secrets.Get(SecretName(setting.SecretRef)),
                    () => testKey is not null || _secrets.Has(SecretName(setting.SecretRef))),
            _ => null,
        };
        if (source is OpenAiCompatibleSource runtime && setting.AddedByAgent && !setting.EndpointConfirmed)
            runtime.MarkUnavailable("waiting for visitor endpoint confirmation");
        return source;
    }

    static string SecretName(string? secretRef) =>
        string.IsNullOrWhiteSpace(secretRef) ? OpenRouterSource.SecretName : secretRef;

    /// <summary>The endpoints the scanner should watch besides the two built-in ports.</summary>
    IEnumerable<(string Kind, string Endpoint)> AddedEndpoints() => _store.Snapshot().Sources
        .Where(s => s.Enabled && s.Kind is SourceKinds.LmStudio or SourceKinds.Ollama or SourceKinds.OpenAiCompatible)
        .Where(s => s.EndpointConfirmed || !s.AddedByAgent && IsLoopbackEndpoint(s.Endpoint))
        .Select(s => (s.Kind!, ScannerEndpoint(s.Kind!, s.Endpoint)));

    static bool IsLoopbackEndpoint(string? endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (OpenAiCompatibleSource.IsLoopback(uri.Host) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    static void NormalizeAddedSourceIds(WalletSettings settings)
    {
        foreach (var source in settings.Sources.Where(s => s.Kind is not null))
        {
            if (source.Kind!.Equals(SourceKinds.OpenRouter, StringComparison.OrdinalIgnoreCase))
                source.Endpoint = "https://openrouter.ai/api/v1";
            if (!source.AddedByAgent && IsLoopbackEndpoint(source.Endpoint)) source.EndpointConfirmed = true;
        }
        var replacements = settings.Sources.Where(s => s.Kind is not null && !string.IsNullOrWhiteSpace(s.Model))
            .Select(s => (Old: s.Id, New: s.Kind!.Equals(SourceKinds.OpenRouter, StringComparison.OrdinalIgnoreCase)
                ? SourceKinds.OpenRouter + ":" + s.Model!.Trim()
                : OpenAiCompatibleSource.MakeId(s.Kind!, s.Model!, s.Endpoint)))
            .Where(pair => pair.Old != pair.New).ToArray();
        foreach (var (oldId, newId) in replacements)
        {
            var source = settings.Sources.FirstOrDefault(s => s.Id == oldId);
            if (source is null || settings.Sources.Any(s => s.Id == newId && s != source)) continue;
            source.Id = newId;
            if (!source.AddedByAgent && Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var uri) &&
                (OpenAiCompatibleSource.IsLoopback(uri.Host) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
                source.EndpointConfirmed = true;
            foreach (var chain in settings.Chains)
                foreach (var stop in chain.Stops.Where(stop => stop.SourceId == oldId)) stop.SourceId = newId;
        }
    }

    /// <summary>The base address a runtime is watched on: the kind's default, or the setting without its /v1 tail.</summary>
    public static string ScannerEndpoint(string kind, string? endpoint)
    {
        if (endpoint?.Trim() is not { Length: > 0 } given) return kind switch
        {
            SourceKinds.LmStudio => RuntimeScanner.LmStudioEndpoint,
            SourceKinds.Ollama => RuntimeScanner.OllamaEndpoint,
            _ => "",
        };
        var address = given.Trim().TrimEnd('/');
        if ((kind is SourceKinds.LmStudio or SourceKinds.Ollama) && address.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            address = address[..^3];
        }

        return address;
    }

    // ------------------------------------------------------------------------------------------------
    // the runtime scan

    /// <summary>Probes the runtimes once now and updates the sources' Up/Listed states (and OpenRouter prices).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var infos = await Scanner.ScanAsync(ct).ConfigureAwait(false);
        var settings = _store.Snapshot();
        foreach (var setting in settings.Sources)
        {
            if (setting is not { Enabled: true, Kind: not null, Model: { Length: > 0 } }) continue;
            var sourceId = OpenAiCompatibleSource.MakeId(setting.Kind, setting.Model, setting.Endpoint);
            if (_text.FirstOrDefault(t => t.Id == sourceId || t.Id == setting.Id) is not OpenAiCompatibleSource source) continue;
            var address = ScannerEndpoint(setting.Kind, setting.Endpoint);
            var info = infos.FirstOrDefault(i => i.Kind == setting.Kind && i.Endpoint.Equals(address, StringComparison.OrdinalIgnoreCase));
            if (info is null || !info.Running)
            {
                source.Up = info?.Running;
                source.Listed = null;
                continue;
            }

            source.Up = true;
            source.Listed = info.Models.Any(m => m.Id == setting.Model);
        }

        var priceTasks = _text.OfType<OpenRouterTextSource>().Select(source => source.PrimePricesAsync(ct)).ToArray();
        if (priceTasks.Length > 0)
        {
            try { await Task.WhenAll(priceTasks).ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                // Price refresh is advisory. A provider keeps its flat estimate when the public list is unavailable.
            }
        }
    }

    async Task ScanLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // a failed scan must not stop the loop; the next one starts in half a minute
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (Exception) { return; }
            }
        }
    }

    public void Dispose()
    {
        _store.Changed -= RebuildText;
        _scans.Cancel();
        try { _scanLoop?.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch (Exception) { /* disposal must not bring down the tray */ }
        _scans.Dispose();
        GigaAm.Dispose();
        Whisper.Dispose();
        WhisperCpu.Dispose();
        TextHttp.Dispose();
    }

    /// <summary>A text-source list the engine may hold forever: replacing the set swaps one array, atomically.</summary>
    sealed class SourceList : IReadOnlyList<ITextSource>
    {
        volatile ITextSource[] _items = [];
        public ITextSource this[int index] => _items[index];
        public int Count => _items.Length;
        public IEnumerator<ITextSource> GetEnumerator() => ((IEnumerable<ITextSource>)_items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Replace(IEnumerable<ITextSource> sources) => _items = sources.ToArray();
    }
}
