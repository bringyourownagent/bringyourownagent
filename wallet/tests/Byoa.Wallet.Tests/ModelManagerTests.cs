using Byoa.Wallet.Activity;
using Byoa.Wallet.Desktop;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;
using System.Net;
using System.Security.Cryptography;

namespace Byoa.Wallet.Tests;

public sealed class ModelManagerTests
{
    [Fact]
    public async Task RegistryDownloadsListsAndRemovesWalletOwnedGigaAmFolder()
    {
        var dir = TestFolder.New();
        var modelsRoot = Path.Combine(dir, "models");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["encoder.int8.onnx"] = [1, 2, 3],
            ["decoder.onnx"] = [4],
            ["joiner.onnx"] = [5, 6],
            ["tokens.txt"] = [7],
            ["LICENSE"] = [8],
        };
        var specs = files.Select(pair => new GigaAmFileSpec(pair.Key, pair.Value.Length,
            Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant())).ToArray();
        using var handler = new GigaHandler(files);
        using var http = new HttpClient(handler);
        using var whisper = new WhisperSource(() => "ggml-small-q8_0.bin", () => [], http);
        using var giga = new GigaAmSource(() => null, http, modelsRoot, new Uri("http://giga.test/"), specs);
        var settings = new SettingsStore(null);
        var manager = new WalletModelManager(settings, whisper, giga, modelsRoot);

        var downloaded = await manager.DownloadAsync("gigaam:gigaam-v3", null, CancellationToken.None);

        Assert.True(downloaded.Ok, downloaded.Message);
        var model = Assert.Single(manager.Models, item => item.Id == "gigaam:gigaam-v3");
        Assert.True(model.Downloaded);
        Assert.Equal(Path.Combine(modelsRoot, "gigaam", GigaAmSource.ModelFolderName), model.OwnedPath);
        var removed = await manager.RemoveAsync(model, CancellationToken.None);
        Assert.True(removed.Ok, removed.Message);
        Assert.False(Directory.Exists(model.OwnedPath));
    }

    [Fact]
    public async Task VisitorGigaAmFolderIsUsedWithoutAWalletDownload()
    {
        var dir = TestFolder.New();
        var modelsRoot = Path.Combine(dir, "models");
        var visitor = Path.Combine(dir, "visitor-giga-am-model");
        Directory.CreateDirectory(visitor);
        foreach (var name in new[] { "encoder.int8.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt" })
            File.WriteAllBytes(Path.Combine(visitor, name), [1]);

        using var handler = new GigaHandler(new Dictionary<string, byte[]>());
        using var http = new HttpClient(handler);
        using var whisper = new WhisperSource(() => "ggml-small-q8_0.bin", () => [], http);
        using var giga = new GigaAmSource(() => visitor, http, modelsRoot);
        var manager = new WalletModelManager(new SettingsStore(null), whisper, giga, modelsRoot);

        var result = await manager.DownloadAsync("gigaam:gigaam-v3", null, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("already available", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(modelsRoot, "gigaam")));
    }

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

    sealed class GigaHandler(IReadOnlyDictionary<string, byte[]> files) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var name = request.RequestUri?.AbsolutePath.Trim('/');
            if (name is null || !files.TryGetValue(name, out var data))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            response.Content.Headers.ContentLength = data.Length;
            return Task.FromResult(response);
        }
    }
}
