using Byoa.Wallet.Activity;
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
        Assert.Equal("lmstudio:qwen2.5:3b", added.Id);

        Assert.True(registry.UpdateSource(added.Id!, new SourceUpdate(Label: "Edited helper", Languages: ["en"])).Ok);
        Assert.True(registry.EnableSource(added.Id!, false).Ok);
        Assert.False(registry.ListSources().Single(s => s.Id == added.Id).Enabled);

        Assert.True(registry.SetOrder(Vocabulary.Generate, "ru-RU", [added.Id!]).Ok);
        var chain = Assert.Single(registry.ListChains(Vocabulary.Generate));
        Assert.Equal("ru", chain.Language);
        Assert.Equal([added.Id!], chain.OrderedSourceIds);

        Assert.True(registry.RemoveSource(added.Id!).Ok);
        Assert.DoesNotContain(registry.ListSources(), s => s.Id == added.Id);
        Assert.Contains(registry.ListChains(Vocabulary.Generate), c => !c.OrderedSourceIds.Contains(added.Id!));
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
