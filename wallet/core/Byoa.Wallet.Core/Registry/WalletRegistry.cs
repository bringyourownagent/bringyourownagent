using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Net;
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
        SourceKinds.LmStudio, SourceKinds.Ollama, SourceKinds.OpenAiCompatible, SourceKinds.OpenRouter, ApiServices.Kind,
    };

    /// <summary>The kinds that sit behind a key and cost money: OpenRouter and the API services.</summary>
    static bool IsPaidKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() is SourceKinds.OpenRouter or ApiServices.Kind;

    /// <summary>A known service has its own address; the one a visitor typed for it is ignored. Only a service he names himself keeps his.</summary>
    static SourceDraft Normalize(SourceDraft draft)
    {
        if (!string.Equals(draft.Kind?.Trim(), ApiServices.Kind, StringComparison.OrdinalIgnoreCase)) return draft;
        var service = draft.Service?.Trim().ToLowerInvariant();
        return ApiServices.Find(service) is { } known ? draft with { Service = known.Id, Endpoint = known.BaseUrl } : draft with { Service = service };
    }

    readonly SettingsStore _store;
    readonly SecretStore _secrets;
    readonly ActivityLog _activity;
    readonly IReadOnlyList<ISpeechSource> _speech;
    readonly IReadOnlyList<ITextSource> _text;
    readonly IRuntimeScanner? _runtimes;
    readonly IRegistrySourceTester? _tester;
    readonly WalletEngine? _engine;
    readonly Func<SourceSetting, ITextSource?>? _textFactory;
    readonly Func<SourceSetting, string?, ITextSource?>? _draftTextFactory;
    readonly Func<SourceSetting, ISpeechSource?>? _speechFactory;
    readonly object _gate = new();

    public WalletRegistry(
        SettingsStore store,
        SecretStore secrets,
        ActivityLog activity,
        IEnumerable<ISpeechSource>? speechSources = null,
        IEnumerable<ITextSource>? textSources = null,
        IRuntimeScanner? runtimeScanner = null,
        IRegistrySourceTester? sourceTester = null,
        WalletEngine? engine = null,
        Func<SourceSetting, ITextSource?>? textFactory = null,
        Func<SourceSetting, string?, ITextSource?>? draftTextFactory = null,
        Func<SourceSetting, ISpeechSource?>? speechFactory = null)
    {
        _store = store;
        _secrets = secrets;
        _activity = activity;
        _speech = speechSources is IReadOnlyList<ISpeechSource> speechList ? speechList : speechSources?.ToArray() ?? [];
        _text = textSources is IReadOnlyList<ITextSource> textList ? textList : textSources?.ToArray() ?? [];
        _runtimes = runtimeScanner;
        _tester = sourceTester;
        _engine = engine;
        _textFactory = textFactory;
        _draftTextFactory = draftTextFactory;
        _speechFactory = speechFactory;
    }

    /// <summary>Raised after a successful management operation, after settings and Activity have been updated.</summary>
    public event Action<RegistryChangedEvent>? Changed;

    /// <summary>Desktop subscribes to this to put the key field in front of the visitor.</summary>
    public event Func<ApiKeyEntryRequest, CancellationToken, Task<RegistryOperationResult>>? ApiKeyEntryRequested;

    /// <summary>Agent-supplied non-loopback endpoints must be confirmed before settings are changed.</summary>
    public event Func<EndpointConfirmationRequest, CancellationToken, Task<bool>>? EndpointConfirmationRequested;

    /// <summary>A full-access agent removing a keyed source must ask the visitor what to do with the key.</summary>
    public event Func<SecretDeletionRequest, CancellationToken, Task<bool>>? SecretDeletionRequested;

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

    public async Task<IReadOnlyList<RegistryRuntimeInfo>> ListRuntimesAsync(CancellationToken ct = default)
    {
        if (_runtimes is null) return [];
        var runtimes = await _runtimes.ScanAsync(ct).ConfigureAwait(false);
        return runtimes.Select(r => new RegistryRuntimeInfo(r.Kind, r.Endpoint, r.Running, r.Models, r.LatencyMs, r.HttpStatus)).ToArray();
    }

    public RegistryOperationResult AddSource(SourceDraft draft, string actor = "you in the window")
    {
        draft = Normalize(draft);
        var validation = ValidateDraft(draft, visitorEntered: true);
        if (validation is not null) return RegistryOperationResult.Failure(validation);
        var endpoint = Endpoint(draft.Kind, draft.Endpoint);
        var assessment = EndpointPolicy.Check(endpoint, agentSupplied: false);
        if (!assessment.Allowed) return RegistryOperationResult.Failure(assessment.Message);
        return AddSourceCore(draft, actor, endpointConfirmed: true, visitorEntered: true, assessment);
    }

    /// <summary>
    /// Adds the visitor's own AI agent as a source of answers (docs/agent-source.md). Only the visitor does this, in the wallet's window: the
    /// program it runs is code the wallet starts, so the MCP bridge has no way to name one (its source kinds do not include <c>agent</c>).
    /// </summary>
    public RegistryOperationResult AddAgentSource(string? label, string flavor, string command, string actor = "you in the window")
    {
        if (!AgentFlavors.Known(flavor)) return RegistryOperationResult.Failure("The wallet can talk to Claude Code and the launchers built on it so far.");
        command = command?.Trim() ?? "";
        if (command.Length == 0) return RegistryOperationResult.Failure("Tell the wallet which program runs your agent.");
        if (!AgentPrograms.IsPlain(command)) return RegistryOperationResult.Failure("The wallet will not run a program whose name has quotes, percent signs or | < > in it.");
        if (AgentPrograms.Resolve(command) is null) return RegistryOperationResult.Failure("The wallet did not find the program " + command + ".");
        var name = CleanLabel(label) ?? AgentFlavors.Name(flavor);
        var slug = AgentPrograms.Slug(name);
        var id = AgentPrograms.IdOf(name);
        lock (_gate)
        {
            if (KnownSourceIds().Contains(id, StringComparer.Ordinal))
                return RegistryOperationResult.Failure("An agent with the name " + name + " is already added.");
            _store.Update(s => s.Sources.Add(new SourceSetting
            {
                Id = id, Kind = SourceKinds.Agent, Model = slug, Label = name, Skills = [Vocabulary.Generate],
                AgentCommand = command, AgentFlavor = flavor, Offer = false, EndpointConfirmed = true,
            }));
        }

        Change("source-added", id, actor, "Added your agent " + name);
        return RegistryOperationResult.Success("Agent added: " + id + ".", id);
    }

    /// <summary>Adds a source requested by an agent, asking the visitor before any remote endpoint is saved.</summary>
    public async Task<RegistryOperationResult> AddSourceAsync(SourceDraft draft, string actor, string agentName,
        CancellationToken ct = default)
    {
        draft = Normalize(draft);
        var validation = ValidateDraft(draft, visitorEntered: false);
        if (validation is not null) return RegistryOperationResult.Failure(validation);
        var endpoint = Endpoint(draft.Kind, draft.Endpoint);
        var assessment = EndpointPolicy.Check(endpoint, agentSupplied: true);
        if (!assessment.Allowed) return RegistryOperationResult.Failure(assessment.Message);
        var needsAgentConfirmation = NeedsAgentEndpointConfirmation(assessment, endpoint);
        var confirmed = !needsAgentConfirmation;
        if (needsAgentConfirmation)
        {
            var handler = EndpointConfirmationRequested;
            if (handler is null) return RegistryOperationResult.Failure("The wallet needs the visitor to confirm this endpoint in a wallet window.");
            assessment = await EndpointPolicy.CheckForConfirmationAsync(endpoint!, ct).ConfigureAwait(false);
            if (!assessment.Allowed) return RegistryOperationResult.Failure(assessment.Message);
            bool accepted;
            try
            {
                accepted = await handler(ToConfirmationRequest(agentName, draft.Model!.Trim(), CleanLabel(draft.Label), assessment), ct).ConfigureAwait(false);
            }
            catch (RegistryPromptBusyException e)
            {
                return RegistryOperationResult.Failure(e.Message);
            }
            if (ct.IsCancellationRequested)
                return RegistryOperationResult.Failure("The agent connection closed before the visitor confirmed this endpoint.");
            if (!accepted)
                return RegistryOperationResult.Failure("The visitor declined this endpoint. Ask them to confirm it in the wallet if they want to use it.");
            confirmed = true;
        }

        return AddSourceCore(draft, actor, confirmed, visitorEntered: false, assessment);
    }

    RegistryOperationResult AddSourceCore(SourceDraft draft, string actor, bool endpointConfirmed, bool visitorEntered,
        EndpointAssessment? assessment = null)
    {
        var skill = SkillOf(draft.Skills);
        var id = MakeId(draft.Kind, draft.Model, draft.Endpoint, skill, draft.Service);
        var endpointOfDraft = Endpoint(draft.Kind, draft.Endpoint);
        lock (_gate)
        {
            if (KnownSourceIds().Contains(id, StringComparer.Ordinal))
                return RegistryOperationResult.Failure("A source with id '" + id + "' already exists.");

            _store.Update(s =>
            {
                s.Sources.Add(new SourceSetting
                {
                    Id = id,
                    Kind = draft.Kind.Trim().ToLowerInvariant(),
                    Model = Clean(draft.Model),
                    Endpoint = Endpoint(draft.Kind, draft.Endpoint),
                    Label = CleanLabel(draft.Label),
                    LabelFromAgent = !visitorEntered && !string.IsNullOrWhiteSpace(draft.Label),
                    AddedByAgent = !visitorEntered,
                    EndpointConfirmed = endpointConfirmed,
                    ConfirmedAddresses = assessment?.ResolvedAddresses.ToList() ?? [],
                    ConfirmedAddressClass = assessment?.AddressClass,
                    Skills = CleanList(draft.Skills),
                    Languages = CleanList(draft.Languages),
                    Service = IsApi(draft.Kind) ? draft.Service?.Trim().ToLowerInvariant() : null,
                    // Every model behind one OpenRouter key or one service's key uses the one key: the visitor enters it once.
                    SecretRef = IsApi(draft.Kind) ? ApiServices.SecretName(draft.Service, endpointOfDraft)
                        : draft.Kind.Trim().Equals(SourceKinds.OpenRouter, StringComparison.OrdinalIgnoreCase) ? "openrouter" : null,
                });
                AddStop(s, id, skill, IsPaidKind(draft.Kind));
            });
        }

        Change("source-added", id, actor, "Added source " + DisplayId(id));
        return RegistryOperationResult.Success("Source added: " + id + ".", id);
    }

    /// <summary>A source that runs the visitor's own agent is a program the wallet starts: it is changed only by the visitor, in the window, never by an agent.</summary>
    static readonly RegistryOperationResult AgentSourceIsTheVisitors = RegistryOperationResult.Failure(
        "A source that runs the visitor's own agent can be changed only by the visitor, in the wallet's window.");

    public RegistryOperationResult UpdateSource(string id, SourceUpdate update, string actor = "you in the window")
    {
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (setting?.Kind?.Equals(SourceKinds.Agent, StringComparison.OrdinalIgnoreCase) == true) return AgentSourceIsTheVisitors;
        var kind = (update.Kind ?? setting?.Kind ?? "").Trim().ToLowerInvariant();
        var endpoint = Endpoint(kind, update.Endpoint ?? setting?.Endpoint);
        var assessment = EndpointPolicy.Check(endpoint, agentSupplied: false);
        if (!assessment.Allowed) return RegistryOperationResult.Failure(assessment.Message);
        return UpdateSourceCore(id, update, actor, endpointConfirmed: true, visitorEntered: true, assessment);
    }

    /// <summary>Edits an agent source, confirming a changed or previously unconfirmed remote endpoint first.</summary>
    public async Task<RegistryOperationResult> UpdateSourceAsync(string id, SourceUpdate update, string actor, string agentName,
        CancellationToken ct = default)
    {
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (setting is null || setting.Kind is null)
            return RegistryOperationResult.Failure("That source is built in or does not exist; only an added source can be edited.");
        if (setting.Kind.Equals(SourceKinds.Agent, StringComparison.OrdinalIgnoreCase)) return AgentSourceIsTheVisitors;
        var kind = (update.Kind ?? setting.Kind).Trim().ToLowerInvariant();
        var endpoint = Endpoint(kind, update.Endpoint ?? setting.Endpoint);
        var assessment = EndpointPolicy.Check(endpoint, agentSupplied: true);
        if (!assessment.Allowed) return RegistryOperationResult.Failure(assessment.Message);
        var sameConfirmedEndpoint = setting.EndpointConfirmed && SameEndpoint(setting.Endpoint, endpoint);
        var needsAgentConfirmation = NeedsAgentEndpointConfirmation(assessment, endpoint) && !sameConfirmedEndpoint;
        var confirmed = !needsAgentConfirmation;
        if (needsAgentConfirmation)
        {
            var handler = EndpointConfirmationRequested;
            if (handler is null) return RegistryOperationResult.Failure("The wallet needs the visitor to confirm this endpoint in a wallet window.");
            assessment = await EndpointPolicy.CheckForConfirmationAsync(endpoint!, ct).ConfigureAwait(false);
            if (!assessment.Allowed) return RegistryOperationResult.Failure(assessment.Message);
            bool accepted;
            try
            {
                accepted = await handler(ToConfirmationRequest(agentName, Clean(update.Model ?? setting.Model) ?? "model", CleanLabel(update.Label ?? setting.Label), assessment), ct).ConfigureAwait(false);
            }
            catch (RegistryPromptBusyException e)
            {
                return RegistryOperationResult.Failure(e.Message);
            }
            if (ct.IsCancellationRequested)
                return RegistryOperationResult.Failure("The agent connection closed before the visitor confirmed this endpoint.");
            if (!accepted)
                return RegistryOperationResult.Failure("The visitor declined this endpoint. Ask them to confirm it in the wallet if they want to use it.");
            confirmed = true;
        }

        return UpdateSourceCore(id, update, actor, confirmed, visitorEntered: false, assessment);
    }

    RegistryOperationResult UpdateSourceCore(string id, SourceUpdate update, string actor, bool endpointConfirmed, bool visitorEntered,
        EndpointAssessment? assessment = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return RegistryOperationResult.Failure("Tell the wallet which source id to update.");
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (setting is null || setting.Kind is null)
            return RegistryOperationResult.Failure("That source is built in or does not exist; only an added source can be edited.");
        if (setting.Kind.Equals(SourceKinds.Agent, StringComparison.OrdinalIgnoreCase)) return AgentSourceIsTheVisitors;
        var kind = (update.Kind ?? setting.Kind).Trim().ToLowerInvariant();
        var model = Clean(update.Model ?? setting.Model);
        if (!Kinds.Contains(kind))
            return RegistryOperationResult.Failure("Unknown source kind. Use lmstudio, ollama, openai-compatible, or openrouter.");
        if (model is null) return RegistryOperationResult.Failure("Tell the wallet the model id before saving this source.");
        var endpoint = Endpoint(kind, update.Endpoint ?? setting.Endpoint);
        if (endpoint is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme is not ("http" or "https"))
            return RegistryOperationResult.Failure("The endpoint must be a complete http or https URL, for example http://127.0.0.1:1234/v1.");

        var oldSkill = SkillOf(setting.Skills);
        var newSkill = SkillOf(update.Skills ?? setting.Skills);
        if (newSkill == Vocabulary.Transcribe && IsPaidKind(kind))
            return RegistryOperationResult.Failure(SpeechNeedsRuntime);
        var newId = MakeId(kind, model, update.Endpoint ?? setting.Endpoint, newSkill, setting.Service);
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
            source.Endpoint = Endpoint(kind, update.Endpoint ?? setting.Endpoint);
            source.EndpointConfirmed = endpointConfirmed;
            if (assessment is not null)
            {
                source.ConfirmedAddresses = assessment.ResolvedAddresses.ToList();
                source.ConfirmedAddressClass = assessment.AddressClass;
            }
            if (!visitorEntered && update.Endpoint is not null) source.AddedByAgent = true;
            if (update.Label is not null)
            {
                source.Label = CleanLabel(update.Label);
                source.LabelFromAgent = !visitorEntered;
            }
            if (update.Skills is not null) source.Skills = CleanList(update.Skills);
            if (update.Languages is not null) source.Languages = CleanList(update.Languages);
            if (!string.Equals(newId, id, StringComparison.Ordinal))
            {
                foreach (var chain in s.Chains)
                    foreach (var stop in chain.Stops.Where(stop => stop.SourceId == id)) stop.SourceId = newId;
            }
            // A source that moved from text to speech (or back) leaves the old skill's route and joins the new one.
            if (oldSkill != newSkill)
                foreach (var chain in s.Chains.Where(c => c.Skill == (oldSkill)))
                    chain.Stops.RemoveAll(stop => stop.SourceId == newId);
            AddStop(s, newId, newSkill, IsPaidKind(kind));
        });
        Change("source-updated", newId, actor, "Updated source " + DisplayId(newId));
        return RegistryOperationResult.Success("Source updated: " + newId + ".", newId);
    }

    public RegistryOperationResult RemoveSource(string id, string actor = "you in the window", bool fullAccess = false,
        bool visitorInitiated = true)
    {
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (setting is null) return RegistryOperationResult.Failure("There is no source with id '" + id + "'.");
        if (setting.Kind is null) return RegistryOperationResult.Failure("Built-in sources cannot be removed. Disable one instead.");
        if (!TryHasKey(id, setting, out var hasKey, out var keyError))
            return RegistryOperationResult.Failure(keyError!);
        if (hasKey && !visitorInitiated && !fullAccess)
            return RegistryOperationResult.Failure("Removing this source would remove its saved key. Ask the visitor to do it in the wallet, or to give you Full access in Settings → Agents.");
        return RemoveSourceCore(id, actor, hasKey && visitorInitiated);
    }

    public async Task<RegistryOperationResult> RemoveSourceAsync(string id, string actor, bool fullAccess,
        CancellationToken ct = default)
    {
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (setting is null) return RegistryOperationResult.Failure("There is no source with id '" + id + "'.");
        if (setting.Kind is null) return RegistryOperationResult.Failure("Built-in sources cannot be removed. Disable one instead.");
        if (!TryHasKey(id, setting, out var hasKey, out var keyError))
            return RegistryOperationResult.Failure(keyError!);
        if (hasKey && !fullAccess)
            return RegistryOperationResult.Failure("Removing this source would remove its saved key. Ask the visitor to do it in the wallet, or to give you Full access in Settings → Agents.");

        var deleteSecret = false;
        if (hasKey)
        {
            var handler = SecretDeletionRequested;
            if (handler is null) return RegistryOperationResult.Failure("The wallet needs the visitor to choose whether to delete the saved key.");
            try
            {
                deleteSecret = await handler(new SecretDeletionRequest(id, DisplayName(setting), actor), ct).ConfigureAwait(false);
            }
            catch (RegistryPromptBusyException e)
            {
                return RegistryOperationResult.Failure(e.Message);
            }
            if (ct.IsCancellationRequested)
                return RegistryOperationResult.Failure("The agent connection closed before the visitor chose what to do with the saved key.");
        }
        return RemoveSourceCore(id, actor, deleteSecret);
    }

    RegistryOperationResult RemoveSourceCore(string id, string actor, bool deleteSecret)
    {
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (setting is null) return RegistryOperationResult.Failure("There is no source with id '" + id + "'.");
        // One key serves every model behind its provider (and the OpenRouter key also serves paid speech): removing one model never takes
        // a key that another model, or the paid speech source, still uses.
        var keyName = SecretName(id, setting);
        if (deleteSecret && (keyName == "openrouter" || _store.Snapshot().Sources.Any(x => x.Id != id && SecretName(x.Id, x) == keyName)))
            deleteSecret = false;
        if (deleteSecret)
        {
            try { _secrets.Remove(SecretName(id, setting)); }
            catch (CryptographicException) { return RegistryOperationResult.Failure("Windows could not open the protected key store. The source was not removed."); }
            catch (IOException) { return RegistryOperationResult.Failure("The wallet could not update its protected key store. The source was not removed."); }
        }
        _store.Update(s =>
        {
            s.Sources.RemoveAll(x => x.Id == id);
            foreach (var chain in s.Chains) chain.Stops.RemoveAll(stop => stop.SourceId == id);
        });
        Change("source-removed", id, actor, "Removed source " + DisplayId(id) + " from the wallet settings" + (deleteSecret ? " and deleted its saved key" : "; its saved key was kept"));
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
        var sources = ListSources();
        var unable = ids.FirstOrDefault(id => sources.FirstOrDefault(source => source.Id == id)?.Skills.Contains(skill, StringComparer.Ordinal) != true);
        if (unable is not null)
            return RegistryOperationResult.Failure("Source '" + unable + "' cannot serve " + skill + ". Choose sources that serve this skill.");
        if (ids.Length == 0)
            return RegistryOperationResult.Failure("Keep at least one source that can serve " + skill + " in the chain.");
        var primary = Primary(language);
        _store.Update(s =>
        {
            var chain = s.Chain(skill, primary);
            var old = chain.Stops.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
            chain.Stops = ids.Select(id => old.TryGetValue(id, out var stop)
                ? stop
                : new ChainStop { SourceId = id }).ToList();
        });
        var changedBy = actor.StartsWith("agent: ", StringComparison.Ordinal)
            ? "agent " + actor[7..] + " changed the order of " + skill
            : "Set the " + skill + (primary is null ? "" : " " + primary) + " source order";
        Change("route-updated", null, actor, changedBy);
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

    public async Task<RegistryTestResult> TestSourceAsync(string sourceId, CancellationToken ct = default)
        => await TestSourceAsync(sourceId, visitorInitiated: false, ct).ConfigureAwait(false);

    /// <summary>
    /// Tests a saved source with a key the visitor has just typed instead of the saved one (he is replacing a key that stopped working).
    /// The key goes only to a temporary source; saving it is a separate step. Without a key this is the plain test of the saved source.
    /// </summary>
    public async Task<RegistryTestResult> TestSourceAsync(string sourceId, bool visitorInitiated, string? apiKey, CancellationToken ct = default)
    {
        if (visitorInitiated && !string.IsNullOrWhiteSpace(apiKey) && _engine is not null && _draftTextFactory is not null
            && KnownSourceIds().Contains(sourceId, StringComparer.Ordinal))
        {
            var setting = _store.Snapshot().Sources.FirstOrDefault(source => source.Id == sourceId);
            if (setting is not null && IsPaidKind(setting.Kind) && SkillOf(setting.Skills) == Vocabulary.Generate)
            {
                var temporary = _draftTextFactory.Invoke(setting, apiKey.Trim());
                if (temporary is not null)
                    return FromEngine(await _engine.TestSourceAsync(temporary, Vocabulary.Generate, visitorInitiated: true, ct).ConfigureAwait(false));
            }
        }

        return await TestSourceAsync(sourceId, visitorInitiated, ct).ConfigureAwait(false);
    }

    /// <summary>Tests a source through the wallet engine; only a visitor pressing Test may authorize a paid call.</summary>
    public async Task<RegistryTestResult> TestSourceAsync(string sourceId, bool visitorInitiated, CancellationToken ct = default)
    {
        if (!KnownSourceIds().Contains(sourceId, StringComparer.Ordinal))
            return new RegistryTestResult(false, "There is no source with id '" + sourceId + "'. Add it first.");

        if (!visitorInitiated)
        {
            var visible = ListSources().FirstOrDefault(source => source.Id == sourceId);
            var setting = _store.Snapshot().Sources.FirstOrDefault(source => source.Id == sourceId);
            if (setting?.AddedByAgent == true && setting.EndpointConfirmed != true)
                return new RegistryTestResult(false, "This agent-added endpoint has not been confirmed by the visitor. Ask the visitor to confirm it in the wallet first.");
        }

        if (_engine is not null)
        {
            if (_speech.Any(source => source.Id == sourceId))
                return FromEngine(await _engine.TestSourceAsync(sourceId, Vocabulary.Transcribe, visitorInitiated, ct).ConfigureAwait(false));

            var text = _text.FirstOrDefault(source => source.Id == sourceId);
            if (text is null)
            {
                var setting = _store.Snapshot().Sources.FirstOrDefault(source => source.Id == sourceId);
                if (setting is not null && SkillOf(setting.Skills) == Vocabulary.Transcribe)
                {
                    // Saved a moment ago: the provider set has not rebuilt its speech list yet.
                    var speech = _speechFactory?.Invoke(setting);
                    return speech is null
                        ? new RegistryTestResult(false, "There is no speech source with id '" + sourceId + "' in the running wallet.")
                        : FromEngine(await _engine.TestSourceAsync(speech, Vocabulary.Transcribe, visitorInitiated, ct).ConfigureAwait(false));
                }

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
        draft = Normalize(draft);                                     // a known service has its own address, as when the source is added
        var validation = ValidateDraft(draft, visitorEntered: visitorInitiated);
        if (validation is not null) return new RegistryTestResult(false, validation);
        var skill = SkillOf(draft.Skills);
        var id = MakeId(draft.Kind, draft.Model, draft.Endpoint, skill);
        var setting = new SourceSetting
        {
            Id = id,
            Kind = draft.Kind.Trim().ToLowerInvariant(),
            Model = Clean(draft.Model),
            Endpoint = Endpoint(draft.Kind, draft.Endpoint),
            Label = Clean(draft.Label),
            Skills = CleanList(draft.Skills),
            Languages = CleanList(draft.Languages),
            // A model behind a service's key cannot be built without saying which service: the test said nothing, so every such test failed
            // with "could not create that text source".
            Service = IsApi(draft.Kind) ? draft.Service?.Trim().ToLowerInvariant() : null,
        };

        if (_engine is not null && skill == Vocabulary.Transcribe)
        {
            var speech = _speechFactory?.Invoke(setting);
            return speech is null
                ? new RegistryTestResult(false, "The wallet could not create that speech source. Check its endpoint and model id.")
                : FromEngine(await _engine.TestSourceAsync(speech, Vocabulary.Transcribe, visitorInitiated, ct).ConfigureAwait(false));
        }

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

    public Task<RegistryOperationResult> SetApiKeyAsync(string sourceId, string key, string actor = "you in the window",
        bool fullAccess = false, bool visitorInitiated = true)
    {
        if (!visitorInitiated && !fullAccess)
            return Task.FromResult(RegistryOperationResult.Failure("Writing an API key supplied through the agent needs Full access. Prefer request_api_key_entry so the visitor types it in the wallet."));
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(RegistryOperationResult.Failure("The API key is empty. Enter it in the wallet and try again."));
        var info = ListSources().FirstOrDefault(s => s.Id == sourceId);
        if (info is null) return Task.FromResult(RegistryOperationResult.Failure("There is no source with id '" + sourceId + "'."));
        var setting = _store.Snapshot().Sources.FirstOrDefault(s => s.Id == sourceId);
        var name = SecretName(sourceId, setting);
        try
        {
            if (_secrets.Has(name) && !visitorInitiated && !fullAccess)
                return Task.FromResult(RegistryOperationResult.Failure("This source already has a saved key. Ask the visitor to replace it in the wallet, or to give you Full access in Settings → Agents."));
        }
        catch (CryptographicException)
        {
            return Task.FromResult(RegistryOperationResult.Failure("Windows could not open the protected key store. Try again in the wallet."));
        }
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

    public async Task<RegistryOperationResult> RequestApiKeyEntryAsync(string sourceId, string actor, CancellationToken ct = default,
        bool fullAccess = false, bool visitorInitiated = true, string? agentId = null)
    {
        var info = ListSources().FirstOrDefault(s => s.Id == sourceId);
        if (info is null) return RegistryOperationResult.Failure("There is no source with id '" + sourceId + "'.");
        var handler = ApiKeyEntryRequested;
        if (handler is null) return RegistryOperationResult.Failure("The wallet cannot open its API-key field right now. Ask the visitor to use Sources in the wallet.");
        return await handler(new ApiKeyEntryRequest(sourceId, info.Name, actor, !visitorInitiated, fullAccess)
        {
            AgentId = !visitorInitiated ? agentId : null,
        }, ct).WaitAsync(ct).ConfigureAwait(false);
    }

    string[] KnownSourceIds() => _speech.Select(s => s.Id).Concat(_text.Select(s => s.Id)).Concat(_store.Snapshot().Sources.Select(s => s.Id)).Distinct(StringComparer.Ordinal).ToArray();

    static bool Matches(string? skill, IReadOnlyList<string> skills) => skill is null || skills.Contains(skill, StringComparer.Ordinal);

    RegistrySourceInfo FromSpeech(ISpeechSource source, IReadOnlyList<string> skills, WalletSettings settings)
    {
        var setting = settings.Sources.FirstOrDefault(s => s.Id == source.Id);
        var added = setting?.Kind is not null;
        return new RegistrySourceInfo(source.Id, added ? DisplayName(setting!) : source.Name, setting?.Kind, setting?.Model, setting?.Endpoint, skills,
            source.Languages ?? setting?.Languages ?? [], source.Runs,
            source.ProviderKind, source.Paid, setting?.Enabled ?? true, true, HasKey(source.Id, setting), source.State.ToString().ToLowerInvariant(), source.StateText);
    }

    RegistrySourceInfo FromText(ITextSource source, IReadOnlyList<string> skills, WalletSettings settings)
    {
        var setting = settings.Sources.FirstOrDefault(s => s.Id == source.Id || AddedSourceId(s) == source.Id);
        var added = setting?.Kind is not null;
        return new RegistrySourceInfo(source.Id, added ? DisplayName(setting!) : source.Name, setting?.Kind, setting?.Model, setting?.Endpoint, skills,
            source.Languages ?? setting?.Languages ?? [], source.Runs,
            source.ProviderKind, source.Paid, setting?.Enabled ?? true, true, HasKey(source.Id, setting), source.State.ToString().ToLowerInvariant(), source.StateText);
    }

    RegistrySourceInfo FromSetting(SourceSetting source, WalletSettings settings)
    {
        var kind = source.Kind!.ToLowerInvariant();
        var skills = source.Skills.Count == 0 ? [Vocabulary.Generate] : source.Skills.ToArray();
        var paid = IsPaidKind(kind);
        var validEndpoint = Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https";
            var loopback = validEndpoint && (OpenAiLoopback(endpoint!.Host) || endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        var runs = paid ? Runs.ApiKey : loopback ? Runs.ThisDevice : "another computer (" + (endpoint?.IdnHost ?? "unknown host") + ")";
        var state = paid && !HasKey(source.Id, source) ? SourceState.NoKey : validEndpoint ? SourceState.Ready : SourceState.Broken;
        var name = DisplayName(source);
        var shownEndpoint = kind == SourceKinds.OpenRouter ? "https://openrouter.ai/api/v1" : source.Endpoint;
        return new RegistrySourceInfo(source.Id, name, kind, source.Model, shownEndpoint, skills, source.Languages.ToArray(), runs,
            paid ? "api-key" : loopback ? "local-model" : "home-server", paid, source.Enabled, false, HasKey(source.Id, source), state.ToString().ToLowerInvariant(),
            paid && !HasKey(source.Id, source) ? "no API key added" : validEndpoint ? "configured; test before use" : "invalid endpoint: use a complete http or https URL");
    }

    bool HasKey(string id, SourceSetting? setting)
    {
        var name = SecretName(id, setting);
        try { return _secrets.Has(name); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    bool TryHasKey(string id, SourceSetting? setting, out bool hasKey, out string? error)
    {
        try
        {
            hasKey = _secrets.Has(SecretName(id, setting));
            error = null;
            return true;
        }
        catch (CryptographicException)
        {
            hasKey = false;
            error = "Windows could not open the protected key store. The source was not removed. Try again in the wallet.";
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            hasKey = false;
            error = "The wallet could not read its protected key store. The source was not removed.";
            return false;
        }
    }

    static string SecretName(string id, SourceSetting? setting) => setting?.SecretRef
        ?? (id == "openrouter-scribe" || setting?.Kind == SourceKinds.OpenRouter ? "openrouter" : "source-key:" + id);

    static string? ValidateDraft(SourceDraft draft, bool visitorEntered)
    {
        var kind = draft.Kind?.Trim().ToLowerInvariant();
        if (kind is null || !Kinds.Contains(kind)) return "Unknown source kind. Use lmstudio, ollama, openai-compatible, openrouter, or api.";
        if (string.IsNullOrWhiteSpace(draft.Model)) return "Tell the wallet the model id before adding the source.";
        if (kind == ApiServices.Kind)
        {
            var service = draft.Service?.Trim().ToLowerInvariant();
            if (service != ApiServices.Custom && ApiServices.Find(service) is null)
                return "Unknown service. Use one of: " + string.Join(", ", ApiServices.All.Select(s => s.Id)) + ", or custom with its https address.";
            if (!Uri.TryCreate(Endpoint(kind, draft.Endpoint), UriKind.Absolute, out var service1)
                || !(service1.Scheme == Uri.UriSchemeHttps || service1.Scheme == Uri.UriSchemeHttp && OpenAiLoopback(service1.Host)))
                return "A model behind an API key needs a complete https address: the key is never sent in the clear.";
        }
        var endpoint = Endpoint(kind, draft.Endpoint);
        var endpointCheck = EndpointPolicy.Check(endpoint, agentSupplied: !visitorEntered);
        if (!endpointCheck.Allowed) return endpointCheck.Message;
        if (draft.Skills is { Count: > 0 } && draft.Skills.Any(string.IsNullOrWhiteSpace)) return "Every skill name must be non-empty.";
        if (draft.Skills is { Count: > 0 } && draft.Skills.Any(skill => skill.Trim() is not (Vocabulary.Generate or Vocabulary.Transcribe)))
            return "Added model sources serve text.generate (text) or audio.transcribe (speech to text).";
        if (draft.Skills is { Count: > 0 } && draft.Skills.Select(skill => skill.Trim()).Distinct(StringComparer.Ordinal).Count() > 1)
            return "Add one source per skill: the same model can be added twice, once for text and once for speech.";
        if (SkillOf(draft.Skills) == Vocabulary.Transcribe && IsPaidKind(kind)) return SpeechNeedsRuntime;
        return null;
    }

    /// <summary>Why a paid API cannot be added as a speech source: the wallet already has its own paid speech source.</summary>
    const string SpeechNeedsRuntime =
        "A speech source is a model in a program on this computer (the Llama app, LM Studio, Ollama) or on a home server. For a paid speech model add your OpenRouter key to Scribe v2.";

    /// <summary>The one skill an added source serves: speech when it says so, text otherwise.</summary>
    static string SkillOf(IReadOnlyList<string>? skills) =>
        skills?.Any(skill => skill.Trim() == Vocabulary.Transcribe) == true ? Vocabulary.Transcribe : Vocabulary.Generate;

    static bool IsApi(string? kind) => string.Equals(kind?.Trim(), ApiServices.Kind, StringComparison.OrdinalIgnoreCase);

    static string MakeId(string kind, string? model, string? endpoint, string skill, string? service = null)
    {
        var normalizedKind = kind.Trim().ToLowerInvariant();
        var normalizedModel = model!.Trim();
        if (normalizedKind == SourceKinds.OpenRouter) return normalizedKind + ":" + normalizedModel;
        if (normalizedKind == ApiServices.Kind) return SourceIds.Api(service ?? "", normalizedModel, Endpoint(normalizedKind, endpoint));
        return SourceIds.OpenAiCompatible(normalizedKind, normalizedModel, Endpoint(normalizedKind, endpoint), skill);
    }

    static string? AddedSourceId(SourceSetting setting) => setting.Kind is null || string.IsNullOrWhiteSpace(setting.Model)
        ? null
        : setting.Kind.ToLowerInvariant() == SourceKinds.OpenRouter
            ? SourceKinds.OpenRouter + ":" + setting.Model.Trim()
            : MakeId(setting.Kind, setting.Model, setting.Endpoint, SkillOf(setting.Skills), setting.Service);
    static string DisplayId(string id) => id.Length > 80 ? id[..77] + "..." : id;
    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static string? CleanLabel(string? value)
        => SourceLabels.Clean(value);

    static bool NeedsAgentEndpointConfirmation(EndpointAssessment assessment, string? endpoint)
    {
        if (IsCanonicalOpenRouter(endpoint)) return false;
        // Every non-loopback endpoint is a different computer from the agent's point of view. Keep this explicit
        // instead of relying only on the assessment flag so future policy changes cannot accidentally skip the UI.
        if (!assessment.IsLoopback && assessment.Uri is not null) return true;
        if (assessment.NeedsConfirmation) return true;
        if (assessment.IsLoopback && !IsStandardRuntimeEndpoint(endpoint)) return true;
        return false;
    }

    static bool IsStandardRuntimeEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        var port = uri.IsDefaultPort ? uri.Scheme == "https" ? 443 : 80 : uri.Port;
        return port is 1234 or 11434;
    }

    static bool IsCanonicalOpenRouter(string? endpoint) => string.Equals(
        endpoint?.Trim().TrimEnd('/'), "https://openrouter.ai/api/v1", StringComparison.OrdinalIgnoreCase);

    static bool SameEndpoint(string? left, string? right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a) || !Uri.TryCreate(right, UriKind.Absolute, out var b))
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        var aPort = a.IsDefaultPort ? a.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80 : a.Port;
        var bPort = b.IsDefaultPort ? b.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80 : b.Port;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
            && aPort == bPort
            && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
            && string.Equals(a.Query, b.Query, StringComparison.Ordinal);
    }

    static EndpointConfirmationRequest ToConfirmationRequest(string agentName, string model, string? label,
        EndpointAssessment assessment)
    {
        var endpoint = assessment.CanonicalEndpoint.Length > 0 ? assessment.CanonicalEndpoint : assessment.Uri?.ToString() ?? "unknown endpoint";
        return new EndpointConfirmationRequest(SourceLabels.Clean(agentName) ?? "your AI agent", endpoint,
            SourceLabels.Clean(model) ?? "model", label)
        {
            CanonicalEndpoint = endpoint,
            Warning = assessment.Uri?.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) == true ? "encrypted" : "not encrypted",
            ResolvedAddresses = assessment.ResolvedAddresses,
            AddressClass = assessment.AddressClass,
            ContainsNonAsciiHost = assessment.ContainsNonAsciiHost,
            IsLoopback = assessment.IsLoopback,
        };
    }

    static string DisplayName(SourceSetting source)
    {
        var label = CleanLabel(source.Label) ?? SourceLabels.Clean(source.Model) ?? source.Kind ?? "source";
        if (Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var uri))
        {
            var loopback = IPAddress.TryParse(uri.Host, out var address) && (IPAddress.IsLoopback(address) ||
                (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()))) ||
                uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            return label + " · " + (loopback ? "this computer" : DisplayAuthority(uri));
        }
        return label;
    }

    static string DisplayAuthority(Uri uri)
    {
        var host = uri.IdnHost;
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal))
            host = "[" + host + "]";
        return host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    static bool OpenAiLoopback(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        return address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
    }
    static string? Endpoint(string kind, string? value)
    {
        if (kind.Trim().Equals(SourceKinds.OpenRouter, StringComparison.OrdinalIgnoreCase))
            return "https://openrouter.ai/api/v1";
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

    /// <summary>Adds an added source to the default route of its skill in the same settings write as the source.</summary>
    static void AddStop(WalletSettings settings, string sourceId, string skill, bool paid)
    {
        if (skill == Vocabulary.Transcribe) AddSpeechStop(settings, sourceId);
        else AddTextStop(settings, sourceId, paid);
    }

    /// <summary>A speech model on this computer answers before the paid fallback.</summary>
    static void AddSpeechStop(WalletSettings settings, string sourceId)
    {
        var chain = settings.Chain(Vocabulary.Transcribe);
        if (chain.Stops.Any(stop => stop.SourceId == sourceId)) return;
        var paid = chain.Stops.FindIndex(stop => stop.SourceId == Router.PaidSpeechId);
        chain.Stops.Insert(paid < 0 ? chain.Stops.Count : paid, new ChainStop { SourceId = sourceId });
    }

    /// <summary>Adds an added model to the default text route in the same settings write as the source.</summary>
    static void AddTextStop(WalletSettings settings, string sourceId, bool paid)
    {
        var chain = settings.Chain(Vocabulary.Generate);
        if (chain.Stops.Any(stop => stop.SourceId == sourceId)) return;
        var before = paid ? chain.Stops.Count : chain.Stops.FindIndex(stop =>
            IsPaidKind(settings.Sources.FirstOrDefault(source => source.Id == stop.SourceId)?.Kind));
        if (before < 0) before = chain.Stops.Count;
        chain.Stops.Insert(before, new ChainStop { SourceId = sourceId });
    }

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
