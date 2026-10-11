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

/// <summary>Builds the three sources of 0.1 from the settings and the secret store.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SourceSet : IDisposable
{
    public SourceSet(SettingsStore store, SecretStore secrets, HttpClient http)
    {
        // A new wallet looks in its own models folder and the usual model folders on this computer.
        if (store.Read(s => s.ModelFolders.Count == 0))
        {
            var defaults = ModelScanner.DefaultFolders();
            store.Update(s => s.ModelFolders.AddRange(defaults));
        }

        Catalog = new ModelCatalog(() => store.Read(s => s.ModelFolders.ToList()));
        GigaAm = new GigaAmSource(() => Catalog.Found.FirstOrDefault(m => m.Kind == "gigaam")?.Path);
        Whisper = new WhisperSource(() => store.Read(s => s.WhisperModel), () => Catalog.Found, http);
        OpenRouter = new OpenRouterSource(http, () => secrets.Get(OpenRouterSource.SecretName), () => secrets.Has(OpenRouterSource.SecretName));
        All = [GigaAm, Whisper, OpenRouter];
    }

    public ModelCatalog Catalog { get; }
    public GigaAmSource GigaAm { get; }
    public WhisperSource Whisper { get; }
    public OpenRouterSource OpenRouter { get; }
    public IReadOnlyList<ISpeechSource> All { get; }

    public void Dispose()
    {
        GigaAm.Dispose();
        Whisper.Dispose();
    }
}
