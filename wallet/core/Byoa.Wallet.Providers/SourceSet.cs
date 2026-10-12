using Byoa.Wallet.Protocol;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

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

    public SourceSet(SettingsStore store, SecretStore secrets, HttpClient http)
    {
        _store = store;
        _secrets = secrets;
        // Text calls go through a client that never follows redirects: an endpoint on this computer must not send
        // the wallet's requests (and the visitor's API key) onward to another computer.
        TextHttp = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = TimeSpan.FromSeconds(150) };

        // A new wallet looks in its own models folder and the usual model folders on this computer.
        if (store.Read(s => s.ModelFolders.Count == 0))
        {
            var defaults = ModelScanner.DefaultFolders();
            defaults.Add(Path.Combine(WalletPaths.Home, "models"));
            store.Update(s => s.ModelFolders.AddRange(defaults));
        }

        Catalog = new ModelCatalog(() => store.Read(s => s.ModelFolders.ToList()));
        GigaAm = new GigaAmSource(() => Catalog.Found.FirstOrDefault(m => m.Kind == "gigaam")?.Path);
        Whisper = new WhisperSource(() => store.Read(s => s.WhisperModel), () => Catalog.Found, http);
        WhisperCpu = new WhisperCpuSource(() => Catalog.Found, http);
        OpenRouter = new OpenRouterSource(http, () => secrets.Get(OpenRouterSource.SecretName), () => secrets.Has(OpenRouterSource.SecretName));
        All = [GigaAm, Whisper, WhisperCpu, OpenRouter];
        Scanner = new RuntimeScanner(TextHttp, AddedEndpoints);
        RebuildText();
        store.Changed += RebuildText;
        _ = Task.Run(() => ScanLoop(_scans.Token));
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

    /// <summary>The text sources as the settings list them. The engine keeps this list for its whole life.</summary>
    public IReadOnlyList<ITextSource> Text => _text;

    /// <summary>The text source with this id ("lmstudio:qwen/qwen3.5-4b"), or null.</summary>
    public ITextSource? FindText(string id) => _text.FirstOrDefault(s => s.Id == id);

    // ------------------------------------------------------------------------------------------------
    // the text sources follow the settings

    void RebuildText()
    {
        if (Interlocked.CompareExchange(ref _rebuilding, 1, 0) == 1) return;
        try
        {
            var settings = _store.Snapshot();
            var list = new List<ITextSource>();
            foreach (var setting in settings.Sources)
            {
                if (!setting.Enabled || !setting.Skills.Contains(Vocabulary.Generate)) continue;
                ITextSource? source = setting.Kind switch
                {
                    SourceKinds.LmStudio or SourceKinds.Ollama
                        when !string.IsNullOrWhiteSpace(setting.Model)
                            => new OpenAiCompatibleSource(setting.Kind, setting.Model, setting.Label, setting.Endpoint, TextHttp),
                    // An "other server" needs an address of its own; the two runtimes above have their usual ports.
                    SourceKinds.OpenAiCompatible
                        when !string.IsNullOrWhiteSpace(setting.Model) && !string.IsNullOrWhiteSpace(setting.Endpoint)
                            => new OpenAiCompatibleSource(setting.Kind, setting.Model, setting.Label, setting.Endpoint, TextHttp),
                    SourceKinds.OpenRouter when !string.IsNullOrWhiteSpace(setting.Model)
                            => new OpenRouterTextSource(setting.Model, setting.Label, setting.SecretRef, TextHttp,
                                () => _secrets.Get(SecretName(setting.SecretRef)), () => _secrets.Has(SecretName(setting.SecretRef))),
                    _ => null,
                };
                if (source is not null && !list.Any(s => s.Id == source.Id)) list.Add(source);
            }

            _text.Replace(list);
            // The chain for text.generate grows with the sources: every one gets a stop, before the first paid one.
            _store.Update(s => Router.EnsureTextChain(s, list));
        }
        finally
        {
            Interlocked.Exchange(ref _rebuilding, 0);
        }
    }

    static string SecretName(string? secretRef) =>
        string.IsNullOrWhiteSpace(secretRef) ? OpenRouterSource.SecretName : secretRef;

    /// <summary>The endpoints the scanner should watch besides the two built-in ports.</summary>
    IEnumerable<(string Kind, string Endpoint)> AddedEndpoints() => _store.Snapshot().Sources
        .Where(s => s.Enabled && s.Kind is SourceKinds.LmStudio or SourceKinds.Ollama or SourceKinds.OpenAiCompatible)
        .Select(s => (s.Kind!, ScannerEndpoint(s.Kind!, s.Endpoint)));

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
        if (kind == SourceKinds.LmStudio && address.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
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
            if (_text.FirstOrDefault(t => t.Id == setting.Kind + ":" + setting.Model) is not OpenAiCompatibleSource source) continue;
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

        foreach (var source in _text.OfType<OpenRouterTextSource>()) _ = source.PrimePricesAsync(ct);
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
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _store.Changed -= RebuildText;
        _scans.Cancel();
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
