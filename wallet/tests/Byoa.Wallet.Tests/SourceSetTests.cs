using Byoa.Wallet.Providers;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The provider set is one live, stable view of added text sources and their settings.</summary>
public sealed class SourceSetTests
{
    [Fact]
    public async Task RebuildKeepsTheListStableHandlesBadEndpointsAndSeparatesRuntimes()
    {
        var home = TestFolder.New();
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        try
        {
            var endpointA = "http://127.0.0.1:5311/v1";
            var endpointB = "http://192.0.2.10:5311/v1";
            var idA = OpenAiCompatibleSource.MakeId(SourceKinds.OpenAiCompatible, "same-model", endpointA);
            var idB = OpenAiCompatibleSource.MakeId(SourceKinds.OpenAiCompatible, "same-model", endpointB);
            var store = new SettingsStore(null, settings => settings.ModelFolders.Add(Path.Combine(home, "models")));
            store.Update(settings => settings.Sources.Add(new SourceSetting
            {
                Id = idA,
                Kind = SourceKinds.OpenAiCompatible,
                Model = "same-model",
                Endpoint = endpointA,
                Skills = [Vocabulary.Generate],
            }));

            using var sources = new SourceSet(store,
                new SecretStore(Path.Combine(home, "secrets.dat")), new HttpClient());
            var stable = sources.Text;
            Assert.Same(stable, sources.Text);
            Assert.NotNull(sources.FindText(idA));
            Assert.Contains(store.Snapshot().Chain(Vocabulary.Generate).Stops, stop => stop.SourceId == idA);

            store.Update(settings => settings.Sources.Add(new SourceSetting
            {
                Id = idB,
                Kind = SourceKinds.OpenAiCompatible,
                Model = "same-model",
                Endpoint = endpointB,
                Skills = [Vocabulary.Generate],
            }));
            Assert.Equal(2, sources.Text.Count);
            Assert.NotNull(sources.FindText(idB));
            Assert.NotEqual(idA, idB);

            // A legacy/custom id cannot make two materialised sources silently replace one another.
            store.Update(settings => settings.Sources.Add(new SourceSetting
            {
                Id = "legacy-duplicate",
                Kind = SourceKinds.OpenAiCompatible,
                Model = "same-model",
                Endpoint = endpointA,
                Skills = [Vocabulary.Generate],
            }));
            Assert.Contains(sources.InvalidTextSources, invalid => invalid.Id == "legacy-duplicate" && invalid.Message.Contains("same id", StringComparison.OrdinalIgnoreCase));

            // The old good source stays routable while a malformed setting is shown to the visitor.
            store.Update(settings => settings.Sources.First(s => s.Id == idA).Endpoint = "not an endpoint");
            Assert.Contains(sources.InvalidTextSources, invalid => invalid.Id == idA && invalid.Message.Contains("http", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(sources.FindText(idA));
            var malformedScan = await sources.Scanner.ScanAsync(default);
            Assert.Contains(malformedScan, info => info.Endpoint == "not an endpoint" && !info.Running);

            store.Update(settings => settings.Sources.First(s => s.Id == idA).Endpoint = endpointA + "/");
            Assert.DoesNotContain(sources.InvalidTextSources, invalid => invalid.Id == idA);
            Assert.NotNull(sources.FindText(idA));
            Assert.Equal(endpointA, SourceSet.ScannerEndpoint(SourceKinds.OpenAiCompatible, endpointA));
            Assert.Equal("http://127.0.0.1:5311", SourceSet.ScannerEndpoint(SourceKinds.LmStudio, endpointA));

            var scan = await sources.Scanner.ScanAsync(default);
            Assert.Contains(scan, info => info.Kind == SourceKinds.OpenAiCompatible && info.Endpoint == endpointA);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
        }
    }
}
