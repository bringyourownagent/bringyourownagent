using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

public sealed class RegistryTests
{
    [Fact]
    public void AddedSourceCanBeEditedEnabledOrderedAndRemoved()
    {
        var dir = TestFolder.New();
        var store = new SettingsStore(null);
        var registry = NewRegistry(store, Path.Combine(dir, "secrets.dat"));
        var added = registry.AddSource(new SourceDraft(SourceKinds.LmStudio, "qwen2.5:3b", "http://127.0.0.1:1234/v1", [Vocabulary.Generate], ["ru"], "Russian helper"), "agent: test");
        Assert.True(added.Ok, added.Message);
        Assert.Equal("lmstudio:qwen2.5:3b@http://127.0.0.1:1234/v1", added.Id);

        Assert.True(registry.UpdateSource(added.Id!, new SourceUpdate(Label: "Edited helper", Languages: ["en"])).Ok);
        Assert.True(registry.EnableSource(added.Id!, false).Ok);
        Assert.False(registry.ListSources().Single(s => s.Id == added.Id).Enabled);

        Assert.True(registry.SetOrder(Vocabulary.Generate, "ru-RU", [added.Id!]).Ok);
        var chain = Assert.Single(registry.ListChains(Vocabulary.Generate), c => c.Language == "ru");
        Assert.Equal("ru", chain.Language);
        Assert.Equal([added.Id!], chain.OrderedSourceIds);

        Assert.True(registry.RemoveSource(added.Id!).Ok);
        Assert.DoesNotContain(registry.ListSources(), s => s.Id == added.Id);
        Assert.Contains(registry.ListChains(Vocabulary.Generate), c => !c.OrderedSourceIds.Contains(added.Id!));
    }

    [Fact]
    public void EditingTheModelIdMovesItsSavedChainStop()
    {
        var dir = TestFolder.New();
        var store = new SettingsStore(null);
        var registry = NewRegistry(store, Path.Combine(dir, "secrets.dat"));
        var added = registry.AddSource(new SourceDraft(SourceKinds.LmStudio, "old-model", Skills: [Vocabulary.Generate]));
        Assert.True(added.Ok, added.Message);
        Assert.True(registry.SetOrder(Vocabulary.Generate, null, [added.Id!]).Ok);

        var edited = registry.UpdateSource(added.Id!, new SourceUpdate(Model: "new-model"));

        Assert.True(edited.Ok, edited.Message);
        Assert.Equal("lmstudio:new-model@http://127.0.0.1:1234/v1", edited.Id);
        Assert.Equal(["lmstudio:new-model@http://127.0.0.1:1234/v1"], Assert.Single(registry.ListChains(Vocabulary.Generate)).OrderedSourceIds);
        Assert.DoesNotContain(registry.ListSources(), source => source.Id == added.Id);
    }

    [Fact]
    public void AgentRouteChangeNamesTheAgentInActivity()
    {
        var dir = TestFolder.New();
        var activity = new ActivityLog(null, DateTimeOffset.UtcNow);
        var store = new SettingsStore(null);
        var registry = new WalletRegistry(store, new SecretStore(Path.Combine(dir, "secrets.dat")), activity,
            [new FakeSource("speech")], [], new FakeRuntimeScanner(), new FakeModelManager([]));
        var added = registry.AddSource(new SourceDraft(SourceKinds.LmStudio, "local", "http://127.0.0.1:1234/v1", [Vocabulary.Generate]));

        Assert.True(added.Ok, added.Message);
        var changed = registry.SetOrder(Vocabulary.Generate, null, [added.Id!], "agent: test runner");

        Assert.True(changed.Ok, changed.Message);
        Assert.Contains(activity.Recent(20), entry => entry.Change == "agent test runner changed the order of text.generate");
    }

    [Fact]
    public async Task ApiKeyIsWriteOnlyFromTheRegistry()
    {
        var dir = TestFolder.New();
        var secretFile = Path.Combine(dir, "secrets.dat");
        var registry = NewRegistry(new SettingsStore(null), secretFile);
        var added = registry.AddSource(new SourceDraft(SourceKinds.OpenRouter, "google/gemma-3-12b-it", "https://openrouter.ai/api/v1", [Vocabulary.Generate]));
        Assert.True(added.Ok, added.Message);

        var result = await registry.SetApiKeyAsync(added.Id!, "secret-value-that-must-not-return");
        Assert.True(result.Ok, result.Message);
        Assert.DoesNotContain("secret-value", result.Message, StringComparison.Ordinal);
        var safe = Assert.Single(registry.ListSources(), s => s.Id == added.Id);
        Assert.True(safe.HasApiKey);
        Assert.DoesNotContain("secret-value", System.Text.Json.JsonSerializer.Serialize(safe), StringComparison.Ordinal);
        Assert.Equal("secret-value-that-must-not-return", new SecretStore(secretFile).Get("source-key:" + added.Id));
    }

    [Fact]
    public void OpenRouterAlwaysUsesItsRealEndpoint()
    {
        var dir = TestFolder.New();
        var registry = NewRegistry(new SettingsStore(null), Path.Combine(dir, "secrets.dat"));

        var added = registry.AddSource(new SourceDraft(SourceKinds.OpenRouter, "test/model", "http://127.0.0.1:5310/v1", [Vocabulary.Generate]));

        Assert.True(added.Ok, added.Message);
        var source = Assert.Single(registry.ListSources(), item => item.Id == added.Id);
        Assert.Equal("https://openrouter.ai/api/v1", source.Endpoint);
    }

    [Fact]
    public void AgentLabelIsCleanedAndItsLocationIsAlwaysShown()
    {
        var dir = TestFolder.New();
        var registry = NewRegistry(new SettingsStore(null), Path.Combine(dir, "secrets.dat"));

        var added = registry.AddSource(new SourceDraft(SourceKinds.OpenAiCompatible, "model", "http://192.168.1.20:5310/v1",
            [Vocabulary.Generate], Label: "  friendly\nname  "));

        Assert.True(added.Ok, added.Message);
        var source = Assert.Single(registry.ListSources(), item => item.Id == added.Id);
        Assert.Equal("friendly name · 192.168.1.20:5310", source.Name);
        Assert.True(registry.ListSources().Single(item => item.Id == added.Id).Name.Length <= 60 + 3 + 32);
    }

    [Fact]
    public async Task ModelRemovalChecksTheActiveWalletFolderBeforeCallingTheManager()
    {
        var dir = TestFolder.New();
        var root = Path.Combine(dir, "models");
        var manager = new FakeModelManager([
            new DownloadableModelInfo("inside", "Inside", Vocabulary.Transcribe, 2, ["en"], "CPU", true, Path.Combine(root, "inside.bin")),
            new DownloadableModelInfo("outside", "Outside", Vocabulary.Transcribe, 2, ["en"], "CPU", true, Path.Combine(dir, "..", "outside.bin")),
        ]);
        var registry = NewRegistry(new SettingsStore(null), Path.Combine(dir, "secrets.dat"), manager, modelsRoot: root);

        Assert.Equal("removed", (await registry.RemoveDownloadedModelAsync("inside")).Message);
        Assert.Equal(1, manager.Removes);
        var outside = await registry.RemoveDownloadedModelAsync("outside");
        Assert.False(outside.Ok);
        Assert.Contains("outside", outside.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, manager.Removes);
    }

    [Fact]
    public async Task ProductionRegistryTestsDraftThroughTheEngineWithoutAnAdapter()
    {
        var dir = TestFolder.New();
        var store = new SettingsStore(null);
        var log = new ActivityLog(null, DateTimeOffset.UtcNow);
        var source = new FakeTextSource("lmstudio:test-model") { Answers = new Queue<string>(["wallet test ok"]) };
        var engine = new WalletEngine(store, [], log, new ScriptedUi(), new ManualTime(), [source]);
        var registry = new WalletRegistry(store, new SecretStore(Path.Combine(dir, "secrets.dat")), log,
            engine: engine, textFactory: _ => source);

        var result = await registry.TestDraftAsync(
            new SourceDraft(SourceKinds.LmStudio, "test-model", "http://127.0.0.1:1234/v1", [Vocabulary.Generate]),
            visitorInitiated: true);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, source.Calls);
        Assert.DoesNotContain(log.Recent(), entry => entry.Outcome == "failed");
    }

    [Fact]
    public async Task DraftTestPassesAVisitorEnteredKeyOnlyToTheTemporarySource()
    {
        var dir = TestFolder.New();
        var store = new SettingsStore(null);
        var log = new ActivityLog(null, DateTimeOffset.UtcNow);
        var source = new FakeTextSource("openrouter:test-model", runs: Sources.Runs.ApiKey, costPerCall: 0.01m)
        {
            Answers = new Queue<string>(["wallet test ok"]),
        };
        string? receivedKey = null;
        var engine = new WalletEngine(store, [], log, new ScriptedUi(), new ManualTime(), [source]);
        var registry = new WalletRegistry(store, new SecretStore(Path.Combine(dir, "secrets.dat")), log,
            engine: engine, draftTextFactory: (setting, key) =>
            {
                receivedKey = key;
                return source;
            });

        var result = await registry.TestDraftAsync(
            new SourceDraft(SourceKinds.OpenRouter, "test-model", Skills: [Vocabulary.Generate]),
            visitorInitiated: true, apiKey: "visitor-key-that-must-not-return");

        Assert.True(result.Ok, result.Message);
        Assert.Equal("visitor-key-that-must-not-return", receivedKey);
        Assert.DoesNotContain("visitor-key", result.Message, StringComparison.Ordinal);
        Assert.Empty(new SecretStore(Path.Combine(dir, "secrets.dat")).Get("source-key:openrouter:test-model") ?? "");
    }

    [Fact]
    public async Task RemoteAgentSourceIsNotSavedOrScannedBeforeVisitorConfirmation()
    {
        var dir = TestFolder.New();
        var settings = new SettingsStore(null);
        var scanner = new CountingRuntimeScanner();
        var registry = new WalletRegistry(settings, new SecretStore(Path.Combine(dir, "secrets.dat")),
            new ActivityLog(null, DateTimeOffset.UtcNow), runtimeScanner: scanner);
        var asked = 0;
        registry.EndpointConfirmationRequested += (_, _) =>
        {
            asked++;
            Assert.Empty(settings.Snapshot().Sources);
            Assert.Equal(0, scanner.Calls);
            return Task.FromResult(asked > 1);
        };

        var draft = new SourceDraft(SourceKinds.OpenAiCompatible, "home-model", "http://192.168.1.20:5310/v1", [Vocabulary.Generate]);
        var declined = await registry.AddSourceAsync(draft, "agent: test", "test agent");
        Assert.False(declined.Ok);
        Assert.Empty(settings.Snapshot().Sources);

        var added = await registry.AddSourceAsync(draft, "agent: test", "test agent");
        Assert.True(added.Ok, added.Message);
        Assert.True(settings.Snapshot().Sources.Single().EndpointConfirmed);
    }

    static WalletRegistry NewRegistry(SettingsStore store, string secretPath, IWalletModelManager? models = null, string? modelsRoot = null)
    {
        var secret = new SecretStore(secretPath);
        var activity = new ActivityLog(null, DateTimeOffset.UtcNow);
        return new WalletRegistry(store, secret, activity, [new FakeSource("fake-local")], [], new FakeRuntimeScanner(), models ?? new FakeModelManager([]), new PassingTester(), modelsRoot);
    }

    sealed class FakeRuntimeScanner : IRuntimeScanner
    {
        public Task<IReadOnlyList<RuntimeInfo>> ScanAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInfo>>([]);
    }

    sealed class CountingRuntimeScanner : IRuntimeScanner
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<RuntimeInfo>> ScanAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RuntimeInfo>>([]);
        }
    }

    sealed class PassingTester : IRegistrySourceTester
    {
        public Task<RegistryTestResult> TestSourceAsync(string sourceId, CancellationToken ct) => Task.FromResult(new RegistryTestResult(true, "ok"));
        public Task<RegistryTestResult> TestDraftAsync(SourceSetting draft, CancellationToken ct) => Task.FromResult(new RegistryTestResult(true, "ok"));
    }

    sealed class FakeModelManager(IReadOnlyList<DownloadableModelInfo> models) : IWalletModelManager
    {
        public IReadOnlyList<DownloadableModelInfo> Models { get; } = models;
        public int Removes { get; private set; }
        public Task<RegistryOperationResult> DownloadAsync(string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken ct) => Task.FromResult(RegistryOperationResult.Success("downloaded"));
        public Task<RegistryOperationResult> RemoveAsync(DownloadableModelInfo model, CancellationToken ct)
        {
            Removes++;
            return Task.FromResult(RegistryOperationResult.Success("removed"));
        }
    }
}

public sealed class HelpParityTests
{
    [Fact]
    public void EveryEmbeddedHelpTopicIsAvailableThroughTheCatalog()
    {
        Assert.NotEmpty(Byoa.Wallet.Help.HelpCatalog.Topics);
        Assert.Contains("overview", Byoa.Wallet.Help.HelpCatalog.Topics);
        foreach (var topic in Byoa.Wallet.Help.HelpCatalog.Topics)
            Assert.False(string.IsNullOrWhiteSpace(Byoa.Wallet.Help.HelpCatalog.Get(topic)));
        Assert.Contains("Topics:", Byoa.Wallet.Help.HelpCatalog.OverviewForAgent());
    }
}
