using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Registry;

/// <summary>
/// The single management surface for sources, models, routes, API-key entry and agent-facing status. The desktop
/// page and the MCP pipe both call this class; they do not edit settings behind each other's back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WalletRegistry
{
    static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        SourceKinds.LmStudio, SourceKinds.Ollama, SourceKinds.OpenAiCompatible, SourceKinds.OpenRouter,
    };

    readonly SettingsStore _store;
    readonly SecretStore _secrets;
    readonly ActivityLog _activity;
    readonly IReadOnlyList<ISpeechSource> _speech;
    readonly IReadOnlyList<ITextSource> _text;
    readonly IRuntimeScanner? _runtimes;
    readonly IWalletModelManager _models;
    readonly IRegistrySourceTester? _tester;
    readonly WalletEngine? _engine;
    readonly Func<SourceSetting, ITextSource?>? _textFactory;
    readonly Func<SourceSetting, string?, ITextSource?>? _draftTextFactory;
    readonly string _modelsRoot;
    readonly object _gate = new();

    public WalletRegistry(
        SettingsStore store,
        SecretStore secrets,
        ActivityLog activity,
        IEnumerable<ISpeechSource>? speechSources = null,
        IEnumerable<ITextSource>? textSources = null,
        IRuntimeScanner? runtimeScanner = null,
        IWalletModelManager? modelManager = null,
        IRegistrySourceTester? sourceTester = null,
        string? modelsRoot = null,
        WalletEngine? engine = null,
        Func<SourceSetting, ITextSource?>? textFactory = null,
        Func<SourceSetting, string?, ITextSource?>? draftTextFactory = null)
    {
        _store = store;
        _secrets = secrets;
        _activity = activity;
        _speech = speechSources is IReadOnlyList<ISpeechSource> speechList ? speechList : speechSources?.ToArray() ?? [];
        _text = textSources is IReadOnlyList<ITextSource> textList ? textList : textSources?.ToArray() ?? [];
        _runtimes = runtimeScanner;
        _models = modelManager ?? new EmptyModelManager();
        _tester = sourceTester;
        _engine = engine;
        _textFactory = textFactory;
        _draftTextFactory = draftTextFactory;
        _modelsRoot = modelsRoot ?? WalletPaths.Models;
    }

    /// <summary>Raised after a successful management operation, after settings and Activity have been updated.</summary>
    public event Action<RegistryChangedEvent>? Changed;

    /// <summary>Desktop subscribes to this to put the key field in front of the visitor.</summary>
    public event Func<ApiKeyEntryRequest, CancellationToken, Task<RegistryOperationResult>>? ApiKeyEntryRequested;

    public IReadOnlyList<string> ServedSkills
    {
        get
        {
            var skills = new HashSet<string>(StringComparer.Ordinal);
            if (_speech.Count > 0) skills.Add(Vocabulary.Transcribe);
            if (_text.Count > 0) skills.Add(Vocabulary.Generate);
            foreach (var source in _store.Snapshot().Sources)
                foreach (var skill in source.Skills.Where(s => !string.IsNullOrWhiteSpace(s))) skills.Add(skill);
            return skills.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<RegistrySourceInfo> ListSources(string? skill = null)
    {
        var settings = _store.Snapshot();
        var result = new List<RegistrySourceInfo>();
        foreach (var source in _speech)
        {
            var skills = new[] { Vocabulary.Transcribe };
            if (Matches(skill, skills)) result.Add(FromSpeech(source, skills, settings));
        }

        foreach (var source in _text)
        {
            var skills = new[] { Vocabulary.Generate };
            if (Matches(skill, skills)) result.Add(FromText(source, skills, settings));
        }

        foreach (var source in settings.Sources.Where(s => s.Kind is not null))
        {
            if (result.Any(x => x.Id == source.Id)) continue;
            var skills = source.Skills.Count == 0 ? [Vocabulary.Generate] : source.Skills.ToArray();
            if (Matches(skill, skills)) result.Add(FromSetting(source, settings));
        }

        return result;
    }

    public IReadOnlyList<RegistryChainInfo> ListChains(string? skill = null)
    {
        var chains = _store.Snapshot().Chains
            .Where(c => skill is null || c.Skill == skill)
            .OrderBy(c => c.Skill, StringComparer.Ordinal)
            .ThenBy(c => c.Language is null ? "" : c.Language, StringComparer.Ordinal)
            .Select(c => new RegistryChainInfo(c.Skill, c.Language, c.Stops.Select(s => s.SourceId).ToArray()))
            .ToArray();
        return chains;
    }

    public IReadOnlyList<DownloadableModelInfo> ListDownloadableModels() => _models.Models;

    public async Task<IReadOnlyList<RegistryRuntimeInfo>> ListRuntimesAsync(CancellationToken ct = default)
    {
        if (_runtimes is null) return [];
        var runtimes = await _runtimes.ScanAsync(ct).ConfigureAwait(false);
        return runtimes.Select(r => new RegistryRuntimeInfo(r.Kind, r.Endpoint, r.Running, r.Models)).ToArray();
    }

    public RegistryOperationResult AddSource(SourceDraft draft, string actor = "you in the window")
    {
        var validation = ValidateDraft(draft);
        if (validation is not null) return RegistryOperationResult.Failure(validation);
        var id = MakeId(draft.Kind, draft.Model, draft.Endpoint);
        lock (_gate)
        {
            if (KnownSourceIds().Contains(id, StringComparer.Ordinal))
                return RegistryOperationResult.Failure("A source with id '" + id + "' already exists.");

            _store.Update(s => s.Sources.Add(new SourceSetting
            {
                Id = id,
                Kind = draft.Kind.Trim().ToLowerInvariant(),
                Model = Clean(draft.Model),
                Endpoint = Endpoint(draft.Kind, draft.Endpoint),
                Label = Clean(draft.Label),
                Skills = CleanList(draft.Skills),
                Languages = CleanList(draft.Languages),
            }));
        }

        Change("source-added", id, actor, "Added source " + DisplayId(id));
        return RegistryOperationResult.Success("Source added: " + id + ".", id);
    }

    public RegistryOperationResult UpdateSource(string id, SourceUpdate update, string actor = "you in the window")
    {
        if (string.IsNullOrWhiteSpace(id)) return RegistryOperationResult.Failure("Tell the wallet which source id to update.");
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (setting is null || setting.Kind is null)
            return RegistryOperationResult.Failure("That source is built in or does not exist; only an added source can be edited.");
        var kind = (update.Kind ?? setting.Kind).Trim().ToLowerInvariant();
        var model = Clean(update.Model ?? setting.Model);
        if (!Kinds.Contains(kind))
            return RegistryOperationResult.Failure("Unknown source kind. Use lmstudio, ollama, openai-compatible, or openrouter.");
        if (model is null) return RegistryOperationResult.Failure("Tell the wallet the model id before saving this source.");

        var newId = MakeId(kind, model, update.Endpoint ?? setting.Endpoint);
        if (!string.Equals(newId, id, StringComparison.Ordinal) && KnownSourceIds().Any(x => x == newId))
            return RegistryOperationResult.Failure("A source with id '" + newId + "' already exists.");

        _store.Update(s =>
        {
            var source = s.Sources.First(x => x.Id == id);
            // The default source-key name contains the source id. Preserve it explicitly when the id changes so
            // editing a model never makes an already entered key disappear from the provider.
            if (!string.Equals(newId, id, StringComparison.Ordinal) && source.SecretRef is null)
                source.SecretRef = "source-key:" + id;
            source.Id = newId;
            source.Kind = kind;
            source.Model = model;
            if (update.Endpoint is not null) source.Endpoint = Clean(update.Endpoint);
            if (update.Label is not null) source.Label = Clean(update.Label);
            if (update.Skills is not null) source.Skills = CleanList(update.Skills);
            if (update.Languages is not null) source.Languages = CleanList(update.Languages);
            if (!string.Equals(newId, id, StringComparison.Ordinal))
            {
                foreach (var chain in s.Chains)
                    foreach (var stop in chain.Stops.Where(stop => stop.SourceId == id)) stop.SourceId = newId;
            }
        });
        Change("source-updated", newId, actor, "Updated source " + DisplayId(newId));
        return RegistryOperationResult.Success("Source updated: " + newId + ".", newId);
    }

    public RegistryOperationResult RemoveSource(string id, string actor = "you in the window")
    {
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (setting is null) return RegistryOperationResult.Failure("There is no source with id '" + id + "'.");
        if (setting.Kind is null) return RegistryOperationResult.Failure("Built-in sources cannot be removed. Disable one instead.");
        try { _secrets.Remove(SecretName(id, setting)); }
        catch (CryptographicException) { return RegistryOperationResult.Failure("Windows could not open the protected key store. The source was not removed."); }
        catch (IOException) { return RegistryOperationResult.Failure("The wallet could not update its protected key store. The source was not removed."); }
        _store.Update(s =>
        {
            s.Sources.RemoveAll(x => x.Id == id);
            foreach (var chain in s.Chains) chain.Stops.RemoveAll(stop => stop.SourceId == id);
        });
        Change("source-removed", id, actor, "Removed source " + DisplayId(id) + " from the wallet settings");
        return RegistryOperationResult.Success("Source removed: " + id + ".", id);
    }

    public RegistryOperationResult EnableSource(string id, bool enabled, string actor = "you in the window")
    {
        if (!KnownSourceIds().Contains(id, StringComparer.Ordinal))
            return RegistryOperationResult.Failure("There is no source with id '" + id + "'.");
        _store.Update(s => s.Source(id).Enabled = enabled);
        Change("source-enabled", id, actor, (enabled ? "Enabled " : "Disabled ") + "source " + DisplayId(id));
        return RegistryOperationResult.Success((enabled ? "Enabled " : "Disabled ") + id + ".", id);
    }

    public RegistryOperationResult SetOrder(string skill, string? language, IReadOnlyList<string> orderedSourceIds, string actor = "you in the window")
    {
        if (string.IsNullOrWhiteSpace(skill)) return RegistryOperationResult.Failure("Tell the wallet which skill needs an order.");
        var ids = orderedSourceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var known = KnownSourceIds();
        var unknown = ids.FirstOrDefault(id => !known.Contains(id, StringComparer.Ordinal));
        if (unknown is not null) return RegistryOperationResult.Failure("There is no source with id '" + unknown + "'. Add it first.");
        var primary = Primary(language);
        _store.Update(s =>
        {
            var chain = s.Chain(skill, primary);
            var old = chain.Stops.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
            chain.Stops = ids.Select(id => old.TryGetValue(id, out var stop)
                ? stop
                : new ChainStop { SourceId = id }).ToList();
        });
        Change("route-updated", null, actor, "Set the " + skill + (primary is null ? "" : " " + primary) + " source order");
        return RegistryOperationResult.Success("Order saved for " + skill + (primary is null ? "" : " (" + primary + ")") + ".");
    }

    public RegistryOperationResult SetStopEnabled(string skill, string? language, string sourceId, bool enabled,
        string actor = "you in the window")
    {
        var primary = Primary(language);
        var chain = _store.Snapshot().Chains.FirstOrDefault(c => c.Skill == skill && c.Language == primary);
        if (chain is null || chain.Stops.All(stop => stop.SourceId != sourceId))
            return RegistryOperationResult.Failure("That source is not in the " + skill + " route. Set its order first.");
        _store.Update(s => s.Chain(skill, primary).Stops.First(stop => stop.SourceId == sourceId).Enabled = enabled);
        Change("route-stop", sourceId, actor, (enabled ? "Enabled " : "Disabled ") + sourceId + " in the " + skill + " route");
        return RegistryOperationResult.Success((enabled ? "Enabled " : "Disabled ") + sourceId + " in the route.", sourceId);
    }

    public RegistryOperationResult SetAskBeforePaid(string skill, string? language, string sourceId, bool ask,
        string actor = "you in the window")
    {
        var primary = Primary(language);
        var chain = _store.Snapshot().Chains.FirstOrDefault(c => c.Skill == skill && c.Language == primary);
        if (chain is null || chain.Stops.All(stop => stop.SourceId != sourceId))
            return RegistryOperationResult.Failure("That source is not in the " + skill + " route. Set its order first.");
        _store.Update(s => s.Chain(skill, primary).Stops.First(stop => stop.SourceId == sourceId).AskBeforePaid = ask);
        Change("paid-approval", sourceId, actor, (ask ? "Ask before paid use of " : "Do not ask before paid use of ") + sourceId);
        return RegistryOperationResult.Success("Paid approval preference saved.", sourceId);
    }

    public async Task<RegistryOperationResult> DownloadModelAsync(string modelId, IProgress<ModelDownloadProgress>? progress = null,
        string actor = "you in the window", CancellationToken ct = default)
    {
        var model = _models.Models.FirstOrDefault(m => m.Id == modelId);
        if (model is null) return RegistryOperationResult.Failure("There is no downloadable model with id '" + modelId + "'.");
        if (!model.CanDownload) return RegistryOperationResult.Failure("The wallet knows this model, but it cannot download it automatically.");
        var result = await _models.DownloadAsync(modelId, progress, ct).ConfigureAwait(false);
        if (result.Ok) Change("model-downloaded", modelId, actor, "Downloaded model " + model.Name);
        return result;
    }

    /// <summary>
    /// Validates confinement before calling the model manager. The model manager performs the second ownership
    /// check immediately before deleting the file.
    /// </summary>
    public async Task<RegistryOperationResult> RemoveDownloadedModelAsync(string modelId, string actor = "you in the window", CancellationToken ct = default)
    {
        var model = _models.Models.FirstOrDefault(m => m.Id == modelId);
        if (model is null) return RegistryOperationResult.Failure("There is no downloaded model with id '" + modelId + "'.");
        if (!model.Downloaded || string.IsNullOrWhiteSpace(model.OwnedPath))
            return RegistryOperationResult.Failure("That model is not a downloaded file owned by this wallet.");
        if (!IsInsideModels(model.OwnedPath, _modelsRoot))
            return RegistryOperationResult.Failure("The wallet refused that path because it is outside its own models folder.");
        var result = await _models.RemoveAsync(model, ct).ConfigureAwait(false);
        if (result.Ok) Change("model-removed", modelId, actor, "Removed downloaded model " + model.Name);
        return result;
    }

    public async Task<RegistryTestResult> TestSourceAsync(string sourceId, CancellationToken ct = default)
        => await TestSourceAsync(sourceId, visitorInitiated: false, ct).ConfigureAwait(false);

    /// <summary>Tests a source through the wallet engine; only a visitor pressing Test may authorize a paid call.</summary>
    public async Task<RegistryTestResult> TestSourceAsync(string sourceId, bool visitorInitiated, CancellationToken ct = default)
    {
        if (!KnownSourceIds().Contains(sourceId, StringComparer.Ordinal))
            return new RegistryTestResult(false, "There is no source with id '" + sourceId + "'. Add it first.");

        if (_engine is not null)
        {
            if (_speech.Any(source => source.Id == sourceId))
                return FromEngine(await _engine.TestSourceAsync(sourceId, Vocabulary.Transcribe, visitorInitiated, ct).ConfigureAwait(false));

            var text = _text.FirstOrDefault(source => source.Id == sourceId);
            if (text is null)
            {
                var setting = _store.Snapshot().Sources.FirstOrDefault(source => source.Id == sourceId);
                if (setting is { Skills.Count: > 0 } && !setting.Skills.Contains(Vocabulary.Generate, StringComparer.Ordinal))
                    return new RegistryTestResult(false, "Added model sources currently serve text.generate; choose Text generation.");
                text = setting is null ? null : _textFactory?.Invoke(setting);
            }

            return text is null
                ? new RegistryTestResult(false, "There is no text source with id '" + sourceId + "' in the running wallet.")
                : FromEngine(await _engine.TestSourceAsync(text, Vocabulary.Generate, visitorInitiated, ct).ConfigureAwait(false));
        }

        if (_tester is null) return new RegistryTestResult(false, "This wallet has no source test adapter.");
        if (visitorInitiated && _tester is IVisitorInitiatedSourceTester visitor)
            return await visitor.TestSourceAsync(sourceId, true, ct).ConfigureAwait(false);
        return await _tester.TestSourceAsync(sourceId, ct).ConfigureAwait(false);
    }

    public async Task<RegistryTestResult> TestDraftAsync(SourceDraft draft, CancellationToken ct = default)
        => await TestDraftAsync(draft, visitorInitiated: false, ct).ConfigureAwait(false);

    /// <summary>Tests a not-yet-saved draft through the same engine path as an existing source.</summary>
    public async Task<RegistryTestResult> TestDraftAsync(SourceDraft draft, bool visitorInitiated, CancellationToken ct = default)
        => await TestDraftAsync(draft, visitorInitiated, apiKey: null, ct).ConfigureAwait(false);

    /// <summary>
    /// Tests a draft with a key the visitor has just entered. The key is passed only to the temporary source; it is
    /// not returned, logged or stored by this method. Saving the source and its key remains a separate operation.
    /// </summary>
    public async Task<RegistryTestResult> TestDraftAsync(SourceDraft draft, bool visitorInitiated, string? apiKey,
        CancellationToken ct = default)
    {
        var validation = ValidateDraft(draft);
        if (validation is not null) return new RegistryTestResult(false, validation);
        if (draft.Skills?.Contains(Vocabulary.Transcribe, StringComparer.Ordinal) == true)
            return new RegistryTestResult(false, "Added model sources currently serve text.generate; choose Text generation.");
        var id = MakeId(draft.Kind, draft.Model, draft.Endpoint);
        var setting = new SourceSetting
        {
            Id = id,
            Kind = draft.Kind.Trim().ToLowerInvariant(),
            Model = Clean(draft.Model),
            Endpoint = Endpoint(draft.Kind, draft.Endpoint),
            Label = Clean(draft.Label),
            Skills = CleanList(draft.Skills),
            Languages = CleanList(draft.Languages),
        };

        if (_engine is not null)
        {
            // A key entered by a caller is meaningful only for the visitor's own Test button. Agent/MCP requests
            // must use the saved source and can never smuggle a paid key into a draft test.
            var text = _draftTextFactory?.Invoke(setting, visitorInitiated ? apiKey : null) ?? _textFactory?.Invoke(setting);
            return text is null
                ? new RegistryTestResult(false, "The wallet could not create that text source. Check its endpoint and model id.")
                : FromEngine(await _engine.TestSourceAsync(text, Vocabulary.Generate, visitorInitiated, ct).ConfigureAwait(false));
        }

        if (_tester is null) return new RegistryTestResult(false, "This wallet has no source test adapter.");
        if (visitorInitiated && _tester is IVisitorInitiatedSourceTester visitor)
            return await visitor.TestDraftAsync(setting, true, ct).ConfigureAwait(false);
        return await _tester.TestDraftAsync(setting, ct).ConfigureAwait(false);
    }

    public Task<RegistryOperationResult> SetApiKeyAsync(string sourceId, string key, string actor = "you in the window")
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(RegistryOperationResult.Failure("The API key is empty. Enter it in the wallet and try again."));
        var info = ListSources().FirstOrDefault(s => s.Id == sourceId);
        if (info is null) return Task.FromResult(RegistryOperationResult.Failure("There is no source with id '" + sourceId + "'."));
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == sourceId);
        var name = SecretName(sourceId, setting);
        // The registry sends the value straight to SecretStore and does not expose a getter or put it in a result.
        try
        {
            _secrets.Set(name, key);
        }
        catch (CryptographicException)
        {
            return Task.FromResult(RegistryOperationResult.Failure("Windows could not protect the API key for this user profile. Try again in the wallet."));
        }
        catch (IOException)
        {
            return Task.FromResult(RegistryOperationResult.Failure("The wallet could not write its protected key store. Check the wallet home and try again."));
        }
        _store.Update(s => s.Source(sourceId).SecretRef = name);
        Change("api-key-saved", sourceId, actor, "Saved an API key for " + info.Name + " (the key is write-only)");
        return Task.FromResult(RegistryOperationResult.Success("API key saved ✓ for " + info.Name + ". It will not be shown again.", sourceId));
    }

    public async Task<RegistryOperationResult> RequestApiKeyEntryAsync(string sourceId, string actor, CancellationToken ct = default)
    {
        var info = ListSources().FirstOrDefault(s => s.Id == sourceId);
        if (info is null) return RegistryOperationResult.Failure("There is no source with id '" + sourceId + "'.");
        var handler = ApiKeyEntryRequested;
        if (handler is null) return RegistryOperationResult.Failure("The wallet cannot open its API-key field right now. Ask the visitor to use Sources in the wallet.");
        return await handler(new ApiKeyEntryRequest(sourceId, info.Name, actor), ct).WaitAsync(ct).ConfigureAwait(false);
    }

    public static bool IsInsideModels(string path, string modelsRoot)
    {
        try
        {
            var root = Path.GetFullPath(modelsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    string[] KnownSourceIds() => _speech.Select(s => s.Id).Concat(_text.Select(s => s.Id)).Concat(_store.Snapshot().Sources.Select(s => s.Id)).Distinct(StringComparer.Ordinal).ToArray();

    static bool Matches(string? skill, IReadOnlyList<string> skills) => skill is null || skills.Contains(skill, StringComparer.Ordinal);

    RegistrySourceInfo FromSpeech(ISpeechSource source, IReadOnlyList<string> skills, WalletSettings settings)
    {
        var setting = settings.Sources.FirstOrDefault(s => s.Id == source.Id);
        return new RegistrySourceInfo(source.Id, source.Name, null, null, null, skills, source.Languages ?? [], source.Runs,
            source.ProviderKind, source.Paid, setting?.Enabled ?? true, true, HasKey(source.Id, setting), source.State.ToString().ToLowerInvariant(), source.StateText);
    }

    RegistrySourceInfo FromText(ITextSource source, IReadOnlyList<string> skills, WalletSettings settings)
    {
        var setting = settings.Sources.FirstOrDefault(s => s.Id == source.Id);
        return new RegistrySourceInfo(source.Id, source.Name, null, null, null, skills, source.Languages ?? [], source.Runs,
            source.ProviderKind, source.Paid, setting?.Enabled ?? true, true, HasKey(source.Id, setting), source.State.ToString().ToLowerInvariant(), source.StateText);
    }

    RegistrySourceInfo FromSetting(SourceSetting source, WalletSettings settings)
    {
        var kind = source.Kind!.ToLowerInvariant();
        var skills = source.Skills.Count == 0 ? [Vocabulary.Generate] : source.Skills.ToArray();
        var paid = kind == SourceKinds.OpenRouter;
        var runs = paid ? Runs.ApiKey : Runs.ThisDevice;
        var state = paid && !HasKey(source.Id, source) ? SourceState.NoKey : SourceState.Ready;
        var name = string.IsNullOrWhiteSpace(source.Label) ? (source.Model ?? kind) : source.Label!;
        return new RegistrySourceInfo(source.Id, name, kind, source.Model, source.Endpoint, skills, source.Languages.ToArray(), runs,
            paid ? "api-key" : "local-model", paid, source.Enabled, false, HasKey(source.Id, source), state.ToString().ToLowerInvariant(),
            paid && !HasKey(source.Id, source) ? "no API key added" : "configured; test before use");
    }

    bool HasKey(string id, SourceSetting? setting)
    {
        var name = SecretName(id, setting);
        try { return _secrets.Has(name); }
        catch (CryptographicException) { return false; }
        catch (IOException) { return false; }
    }

    static string SecretName(string id, SourceSetting? setting) => setting?.SecretRef
        ?? (id == "openrouter-scribe" ? "openrouter" : "source-key:" + id);

    static string? ValidateDraft(SourceDraft draft)
    {
        var kind = draft.Kind?.Trim().ToLowerInvariant();
        if (kind is null || !Kinds.Contains(kind)) return "Unknown source kind. Use lmstudio, ollama, openai-compatible, or openrouter.";
        if (string.IsNullOrWhiteSpace(draft.Model)) return "Tell the wallet the model id before adding the source.";
        if (draft.Skills is { Count: > 0 } && draft.Skills.Any(string.IsNullOrWhiteSpace)) return "Every skill name must be non-empty.";
        if (draft.Skills is { Count: > 0 } && draft.Skills.Any(skill => !string.Equals(skill, Vocabulary.Generate, StringComparison.Ordinal)))
            return "Added model sources currently serve text.generate; choose Text generation.";
        return null;
    }

    static string MakeId(string kind, string? model, string? endpoint) => kind.Trim().ToLowerInvariant() + ":" + model!.Trim();
    static string DisplayId(string id) => id.Length > 80 ? id[..77] + "..." : id;
    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    static string? Endpoint(string kind, string? value)
    {
        var clean = Clean(value)?.TrimEnd('/');
        if (clean is not null) return clean;
        return kind.Trim().ToLowerInvariant() switch
        {
            SourceKinds.LmStudio => "http://127.0.0.1:1234/v1",
            SourceKinds.Ollama => "http://127.0.0.1:11434/v1",
            SourceKinds.OpenRouter => "https://openrouter.ai/api/v1",
            _ => null,
        };
    }
    static List<string> CleanList(IReadOnlyList<string>? values) => values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList() ?? [];
    static string? Primary(string? language) => Vocabulary.PrimaryLanguage(language);

    static RegistryTestResult FromEngine(JsonNode? result)
    {
        if (result is JsonObject { } json && json["ok"]?.GetValue<bool>() == true)
            return new RegistryTestResult(true, "Test passed: the wallet routed the short sample through this source.",
                json["output"]?["text"]?.GetValue<string>());

        var reason = result is JsonObject failed && failed["error"]?.GetValue<string>() is { } error
            ? error : "the source did not answer";
        return new RegistryTestResult(false, "Test failed: " + reason + ".");
    }

    void Change(string action, string? sourceId, string actor, string text)
    {
        _activity.Add(new ActivityEntry
        {
            At = DateTimeOffset.UtcNow,
            Origin = "wallet",
            Kind = "change",
            Outcome = "ok",
            Actor = actor,
            Change = text,
            Note = text,
            Source = sourceId,
        });
        Changed?.Invoke(new RegistryChangedEvent(action, sourceId, actor));
    }
}
