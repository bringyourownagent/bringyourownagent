using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;
using System.Net;
using System.Net.Sockets;
using System.Collections.ObjectModel;

namespace Byoa.Wallet.Providers;

/// <summary>An added source setting (text or speech) that the provider set could not construct.</summary>
public sealed record InvalidTextSource(string Id, string Label, string Message);

/// <summary>
/// Builds the wallet's sources from the settings and the secret store. The wallet holds no model: every source is a
/// program that runs one (a model in the Llama app, LM Studio or Ollama, an OpenAI-compatible server) or a paid API
/// behind the visitor's own key. One speech source or one text source per added source in the settings, plus the paid
/// OpenRouter speech source. The two lists are stable — the engine holds each once and never sees a half-built set —
/// and rebuild themselves whenever the settings change. A background scan keeps the "is the runtime running" states
/// fresh.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SourceSet : IDisposable
{
    readonly SettingsStore _store;
    readonly SecretStore _secrets;
    readonly CancellationTokenSource _scans = new();
    readonly SourceList<ITextSource> _text = new();
    readonly SourceList<ISpeechSource> _speech = new();
    int _rebuilding;
    int _rebuildPending;
    Task? _scanLoop;
    readonly object _sourceGate = new();
    readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveEndpoint;
    Dictionary<string, ITextSource> _lastGood = new(StringComparer.Ordinal);
    Dictionary<string, ISpeechSource> _lastGoodSpeech = new(StringComparer.Ordinal);
    IReadOnlyList<InvalidTextSource> _invalid = [];

    /// <param name="catalogPath">Where the last model list is kept on disk; null keeps none (tests).</param>
    public SourceSet(SettingsStore store, SecretStore secrets, HttpClient http,
        Func<string, CancellationToken, Task<IPAddress[]>>? endpointResolver = null, string? catalogPath = null)
    {
        _store = store;
        _secrets = secrets;
        _resolveEndpoint = endpointResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
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
            // The engine ends a call by its own limit (up to ten minutes); a client timeout would cut a long answer short.
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = BoundedHttp.MaxJsonBytes,
        };

        OpenRouter = new OpenRouterSource(http, () => secrets.Get(OpenRouterSource.SecretName), () => secrets.Has(OpenRouterSource.SecretName));
        Catalog = new OpenRouterCatalog(TextHttp, catalogPath);
        _refreshCatalog = catalogPath is not null;                          // a wallet under test never reaches for the network
        Scanner = new RuntimeScanner(TextHttp, AddedEndpoints);
        // Older settings used an Ollama id without /v1, and carried the speech engines that were once built into the
        // wallet. Canonicalise the ids and drop those engines once, before route repair.
        _store.UpdateIfChanged(Router.EnsureDefaults);
        _store.UpdateIfChanged(NormalizeAddedSourceIds);
        // The registry normally adds the route in the same settings update as the source. For older settings,
        // repair the routes once before subscribing to Changed; a rebuild must never write settings.json again.
        _store.UpdateIfChanged(Router.EnsureTextChainFromSettings);
        _store.UpdateIfChanged(Router.EnsureSpeechChainFromSettings);
        Rebuild();
        store.Changed += Rebuild;
        _scanLoop = Task.Run(() => ScanLoop(_scans.Token));
    }

    public OpenRouterSource OpenRouter { get; }

    /// <summary>The public model list (names and prices): what the pickers search and what priced calls use. Refreshed once a day.</summary>
    public OpenRouterCatalog Catalog { get; }

    readonly bool _refreshCatalog;

    /// <summary>The speech sources as the settings list them, the paid OpenRouter one last. The engine keeps this list for its whole life.</summary>
    public IReadOnlyList<ISpeechSource> All => _speech;

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
        foreach (var source in _speech.OfType<OpenAiCompatibleSpeechSource>())
            if (EndpointMatches(source.Endpoint, host, port)) source.MarkUnavailable(reason);
    }

    void MarkEndpointAvailable(string host, int port)
    {
        foreach (var source in _text.OfType<OpenAiCompatibleSource>())
            if (EndpointMatches(source.Endpoint, host, port)) source.MarkAvailable();
        foreach (var source in _speech.OfType<OpenAiCompatibleSpeechSource>())
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

    /// <summary>The speech source with this id, or null.</summary>
    public ISpeechSource? FindSpeech(string id) => _speech.FirstOrDefault(s => s.Id == id);

    /// <summary>Added source settings that could not be materialised; the Sources page can show the readable reason.</summary>
    public IReadOnlyList<InvalidTextSource> InvalidTextSources
    {
        get { lock (_sourceGate) return new ReadOnlyCollection<InvalidTextSource>(_invalid.ToList()); }
    }

    // ------------------------------------------------------------------------------------------------
    // the sources follow the settings

    void Rebuild()
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
                    if (setting.Kind is null || setting.Skills.Count > 0 && !setting.Skills.Contains(Vocabulary.Generate)) continue;
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

                var speech = new List<ISpeechSource>();
                foreach (var setting in settings.Sources.Where(s => s.Kind is not null && s.Skills.Contains(Vocabulary.Transcribe)))
                {
                    try
                    {
                        var source = CreateSpeechSource(setting);
                        if (source is null)
                        {
                            invalid.Add(new InvalidTextSource(setting.Id, setting.Label ?? setting.Model ?? setting.Id,
                                "A speech source needs a model in a program on this computer or on a home server."));
                            continue;
                        }

                        if (speech.Any(s => s.Id == source.Id))
                        {
                            invalid.Add(new InvalidTextSource(setting.Id, setting.Label ?? setting.Model ?? setting.Id,
                                "This source has the same id as another source."));
                            continue;
                        }

                        speech.Add(source);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        invalid.Add(new InvalidTextSource(setting.Id, setting.Label ?? setting.Model ?? setting.Id, e.Message));
                        lock (_sourceGate)
                        {
                            if (_lastGoodSpeech.TryGetValue(setting.Id, out var old) && !speech.Any(s => s.Id == old.Id)) speech.Add(old);
                        }
                    }
                }

                speech.Add(OpenRouter);
                _text.Replace(list);
                _speech.Replace(speech);
                lock (_sourceGate)
                {
                    _lastGood = list.ToDictionary(s => s.Id, StringComparer.Ordinal);
                    _lastGoodSpeech = speech.ToDictionary(s => s.Id, StringComparer.Ordinal);
                    _invalid = invalid;
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A malformed setting, serializer problem, or a provider constructor must never escape SettingsStore.Update.
            lock (_sourceGate)
                _invalid = [new InvalidTextSource("settings", "Sources", e.Message)];
        }
        finally
        {
            Interlocked.Exchange(ref _rebuilding, 0);
            if (Volatile.Read(ref _rebuildPending) == 1) Rebuild();
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
        // His own agent: a program on this computer, not an address. Only the visitor sets its program (the MCP bridge cannot add one).
        if (kind == SourceKinds.Agent)
        {
            return string.IsNullOrWhiteSpace(setting.AgentCommand) ? null
                : new AgentCliSource(new AgentCliSource.SourceSettingView(setting.Id, setting.Model.Trim(), setting.Label, setting.AgentFlavor ?? "", setting.AgentCommand.Trim(),
                        string.IsNullOrWhiteSpace(setting.AgentModel) ? null : setting.AgentModel.Trim()),
                    Path.Combine(WalletPaths.Home, "agent-work"));
        }

        var endpoint = kind == SourceKinds.OpenRouter
            ? "https://openrouter.ai/api/v1"
            : kind == ApiServices.Kind
                ? setting.Endpoint?.Trim() is { Length: > 0 } given ? given : ApiServices.Find(setting.Service)?.BaseUrl ?? ""
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
                    () => testKey is not null || _secrets.Has(SecretName(setting.SecretRef)), catalog: () => Catalog),
            ApiServices.Kind
                when !string.IsNullOrWhiteSpace(setting.Service)
                => new CloudChatSource(setting.Service.Trim().ToLowerInvariant(), setting.Model.Trim(), setting.Label, endpoint, TextHttp,
                    () => testKey ?? _secrets.Get(ApiSecretName(setting)),
                    () => testKey is not null || _secrets.Has(ApiSecretName(setting)), () => Catalog),
            _ => null,
        };
        if (source is OpenAiCompatibleSource runtime && setting.AddedByAgent && !setting.EndpointConfirmed)
            runtime.MarkUnavailable("waiting for visitor endpoint confirmation");
        return source;
    }

    /// <summary>
    /// Materialises one speech source: a model in the Llama app, LM Studio, Ollama or another OpenAI-compatible server.
    /// A paid API is not a speech source of this kind (the wallet has its own paid speech source).
    /// </summary>
    public ISpeechSource? CreateSpeechSource(SourceSetting setting)
    {
        if (setting.Kind is null || string.IsNullOrWhiteSpace(setting.Model)) return null;
        var kind = setting.Kind.Trim().ToLowerInvariant();
        var endpoint = setting.Endpoint?.Trim() is { Length: > 0 } configured ? configured : OpenAiCompatibleSource.DefaultEndpoint(kind);
        if (kind is not (SourceKinds.LmStudio or SourceKinds.Ollama or SourceKinds.OpenAiCompatible) ||
            kind == SourceKinds.OpenAiCompatible && string.IsNullOrWhiteSpace(setting.Endpoint)) return null;
        var endpointPolicy = EndpointPolicy.Check(endpoint, agentSupplied: false);
        if (!endpointPolicy.Allowed) throw new ArgumentException(endpointPolicy.Message);
        var source = new OpenAiCompatibleSpeechSource(setting.Kind, setting.Model.Trim(), setting.Label, setting.Endpoint, setting.Languages, TextHttp);
        if (setting.AddedByAgent && !setting.EndpointConfirmed) source.MarkUnavailable("waiting for visitor endpoint confirmation");
        return source;
    }

    static string SecretName(string? secretRef) =>
        string.IsNullOrWhiteSpace(secretRef) ? OpenRouterSource.SecretName : secretRef;

    /// <summary>The key an API-service source uses: the one saved for its service (shared by every model behind it), unless the setting names another.</summary>
    public static string ApiSecretName(SourceSetting setting) =>
        !string.IsNullOrWhiteSpace(setting.SecretRef) ? setting.SecretRef : ApiSecretName(setting.Service, setting.Endpoint);

    public static string ApiSecretName(string? service, string? endpoint) => ApiServices.SecretName(service, endpoint);

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
        var replacements = settings.Sources.Where(s => s.Kind is not null && !string.IsNullOrWhiteSpace(s.Model)
                && !s.Kind.Equals(SourceKinds.Agent, StringComparison.OrdinalIgnoreCase))
            .Select(s => (Old: s.Id, New: s.Kind!.Equals(SourceKinds.OpenRouter, StringComparison.OrdinalIgnoreCase)
                ? SourceKinds.OpenRouter + ":" + s.Model!.Trim()
                : s.Kind.Equals(ApiServices.Kind, StringComparison.OrdinalIgnoreCase)
                    ? SourceIds.Api(s.Service ?? "", s.Model!, s.Endpoint)
                    : OpenAiCompatibleSource.MakeId(s.Kind!, s.Model!, s.Endpoint, SkillOf(s))))
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

    /// <summary>The one skill an added source serves: speech when it says so, text otherwise.</summary>
    static string SkillOf(SourceSetting setting) =>
        setting.Skills.Contains(Vocabulary.Transcribe) ? Vocabulary.Transcribe : Vocabulary.Generate;

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
        _ = await Catalog.RefreshAsync(ct).ConfigureAwait(false);          // at most once a day, and never an error
        var settings = _store.Snapshot();
        foreach (var setting in settings.Sources)
        {
            if (setting is not { Enabled: true, Kind: not null, Model: { Length: > 0 } }) continue;
            var sourceId = OpenAiCompatibleSource.MakeId(setting.Kind, setting.Model, setting.Endpoint, SkillOf(setting));
            var address = ScannerEndpoint(setting.Kind, setting.Endpoint);
            var info = infos.FirstOrDefault(i => i.Kind == setting.Kind && i.Endpoint.Equals(address, StringComparison.OrdinalIgnoreCase));
            var running = info is { Running: true };
            var up = running ? true : info?.Running;
            var listed = running ? info!.Models.Any(m => m.Id == setting.Model) : (bool?)null;
            var model = running ? info!.Models.FirstOrDefault(m => m.Id == setting.Model) : null;
            bool? loaded = model is { LoadedKnown: true } ? model.Loaded : null;           // null: the runtime does not say what is in memory
            if (SkillOf(setting) == Vocabulary.Transcribe)
            {
                if (_speech.FirstOrDefault(t => t.Id == sourceId || t.Id == setting.Id) is not OpenAiCompatibleSpeechSource speech) continue;
                speech.Up = up;
                speech.Listed = listed;
                speech.Loaded = loaded;
            }
            else
            {
                if (_text.FirstOrDefault(t => t.Id == sourceId || t.Id == setting.Id) is not OpenAiCompatibleSource source) continue;
                source.Up = up;
                source.Listed = listed;
                source.Loaded = loaded;
            }
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
        _store.Changed -= Rebuild;
        _scans.Cancel();
        try { _scanLoop?.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch (Exception) { /* disposal must not bring down the tray */ }
        _scans.Dispose();
        TextHttp.Dispose();
    }

    /// <summary>A source list the engine may hold forever: replacing the set swaps one array, atomically.</summary>
    sealed class SourceList<T> : IReadOnlyList<T>
    {
        volatile T[] _items = [];
        public T this[int index] => _items[index];
        public int Count => _items.Length;
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Replace(IEnumerable<T> sources) => _items = sources.ToArray();
    }
}
