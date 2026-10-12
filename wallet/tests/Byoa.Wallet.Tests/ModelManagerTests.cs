using Byoa.Wallet.Activity;
using Byoa.Wallet.Desktop;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

public sealed class ModelManagerTests
{
    [Fact]
    public async Task RegistryDeletesOnlyAnOwnedModelInTheTestHome()
    {
        var dir = TestFolder.New();
        var modelsRoot = Path.Combine(dir, "models");
        var whisperRoot = Path.Combine(modelsRoot, "whisper");
        Directory.CreateDirectory(whisperRoot);
        File.WriteAllText(Path.Combine(whisperRoot, WalletModelManager.OwnershipMarker), "BYOA wallet-owned model folder\n");
        const string file = "ggml-small-q8_0.bin";
        var path = Path.Combine(whisperRoot, file);
        File.WriteAllBytes(path, [1, 2, 3]);

        var settings = new SettingsStore(null);
        settings.Update(s => s.DownloadedModelFiles.Add(file));
        using var http = new HttpClient();
        using var whisper = new WhisperSource(() => file, () => [], http);
        var manager = new WalletModelManager(settings, whisper, modelsRoot);
        var activity = new ActivityLog(null, DateTimeOffset.UtcNow);
        var registry = new WalletRegistry(settings, new Byoa.Wallet.Secrets.SecretStore(Path.Combine(dir, "secrets.dat")), activity,
            modelManager: manager, modelsRoot: modelsRoot);

        var model = Assert.Single(registry.ListDownloadableModels(), m => m.Id == "whisper:" + file);
        Assert.True(model.Downloaded);
        var result = await registry.RemoveDownloadedModelAsync(model.Id);

        Assert.True(result.Ok, result.Message);
        Assert.False(File.Exists(path));
        Assert.Contains(activity.Recent(10), entry => entry.Change?.Contains("Removed downloaded model", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ManagerRefusesAFileInAnUnownedWhisperFolder()
    {
        var dir = TestFolder.New();
        var modelsRoot = Path.Combine(dir, "models");
        var whisperRoot = Path.Combine(modelsRoot, "whisper");
        Directory.CreateDirectory(whisperRoot);
        const string file = "ggml-small-q8_0.bin";
        var path = Path.Combine(whisperRoot, file);
        File.WriteAllBytes(path, [1, 2, 3]);

        var settings = new SettingsStore(null);
        settings.Update(s => s.DownloadedModelFiles.Add(file));
        using var http = new HttpClient();
        using var whisper = new WhisperSource(() => file, () => [], http);
        var manager = new WalletModelManager(settings, whisper, modelsRoot);
        var result = await manager.RemoveAsync(new DownloadableModelInfo("whisper:" + file, "Whisper small", Vocabulary.Transcribe,
            3, Vocabulary.WhisperLanguages, "CPU", true, path), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("did not download", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }
}
