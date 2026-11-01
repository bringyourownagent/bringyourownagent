using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The new fields of a source's setting: what it can do. An older wallet's settings file still loads (also one that has the retired
/// "alsoText" tick, which is ignored and never written), and the new fields are not written while they say nothing.</summary>
public class SourcePropertySettingsTests
{
    [Fact]
    public void ASettingsFileOfAnOlderWalletLoadsAsBefore()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """
            {
              "version": 1,
              "sources": [
                { "id": "openrouter:google/gemini-3.1-flash-image", "enabled": true, "kind": "openrouter",
                  "model": "google/gemini-3.1-flash-image", "endpoint": "https://openrouter.ai/api/v1",
                  "skills": ["text.generate"], "offer": false, "thinking": true, "thinkingBudget": 2000 },
                { "id": "openrouter:someone/imagine", "enabled": true, "kind": "openrouter", "model": "someone/imagine",
                  "skills": ["text.generate"], "outputs": ["image"], "alsoText": true }
              ]
            }
            """);
        var settings = new SettingsStore(path).Snapshot();
        Assert.Equal(2, settings.Sources.Count);
        var source = settings.Sources[0];
        Assert.Null(source.Inputs);
        Assert.Null(source.Outputs);                   // nothing was said: the catalog speaks
        Assert.Equal(["image"], settings.Sources[1].Outputs);      // what the file did say is kept; the retired "alsoText" is read past
    }

    [Fact]
    public void TheRetiredAlsoTextTickIsIgnoredAndNeverWrittenAgain()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """
            { "version": 1, "sources": [ { "id": "openrouter:someone/imagine", "kind": "openrouter", "model": "someone/imagine", "alsoText": true } ] }
            """);
        var store = new SettingsStore(path);
        store.Update(s => s.Source("openrouter:someone/imagine").Thinking = false);          // any update writes the file
        Assert.DoesNotContain("alsoText", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhatTheVisitorSaysIsKeptAndEmptyIsNotWritten()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "settings.json");
        var store = new SettingsStore(path);
        store.Update(s => s.Source("openrouter:someone/imagine").Outputs = ["image"]);
        var written = File.ReadAllText(path);
        Assert.Contains("\"outputs\": [", written);
        Assert.DoesNotContain("inputs", written);      // the visitor said nothing about the input: the key stays out of the file

        var again = new SettingsStore(path).Snapshot().Source("openrouter:someone/imagine");
        Assert.Equal(["image"], again.Outputs);

        // Taken back, they go from the file again; so does a list that was emptied.
        store.Update(s =>
        {
            s.Source("openrouter:someone/imagine").Outputs = [];
            s.Source("openrouter:someone/imagine").Inputs = [];
        });
        var cleared = File.ReadAllText(path);
        Assert.DoesNotContain("outputs", cleared);
        Assert.DoesNotContain("inputs", cleared);
    }

    [Fact]
    public void ASourceThatSaysNothingComesBackSayingNothingThroughTheSnapshot()
    {
        // Snapshot() goes through the same serializer as the file.
        var store = new SettingsStore(null);
        store.Update(s => s.Sources.Add(new SourceSetting { Id = "lmstudio:qwen", Kind = SourceKinds.LmStudio, Model = "qwen/qwen3.5-4b" }));
        var source = Assert.Single(store.Snapshot().Sources);
        Assert.Null(source.Inputs);
        Assert.Null(source.Outputs);
    }
}
