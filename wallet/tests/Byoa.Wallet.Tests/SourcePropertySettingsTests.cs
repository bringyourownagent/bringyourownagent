using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The new fields of a source's setting: what it can do, and the "also use as a text model" tick. An older wallet's settings file
/// still loads, and the new fields are not written while they say nothing.</summary>
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
                  "skills": ["text.generate"], "offer": false, "thinking": true, "thinkingBudget": 2000 }
              ]
            }
            """);
        var settings = new SettingsStore(path).Snapshot();
        var source = Assert.Single(settings.Sources);
        Assert.Null(source.Inputs);
        Assert.Null(source.Outputs);
        Assert.False(source.AlsoText);                 // nothing was said: the catalog speaks, and the tick is off
    }

    [Fact]
    public void WhatTheVisitorSaysIsKeptAndEmptyIsNotWritten()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "settings.json");
        var store = new SettingsStore(path);
        store.Update(s => s.Source("openrouter:someone/imagine").Outputs = ["image"]);
        store.Update(s => s.Source("openrouter:someone/imagine").AlsoText = true);
        var written = File.ReadAllText(path);
        Assert.Contains("\"outputs\": [", written);
        Assert.Contains("\"alsoText\": true", written);
        Assert.DoesNotContain("inputs", written);      // the visitor said nothing about the input: the key stays out of the file

        var again = new SettingsStore(path).Snapshot().Source("openrouter:someone/imagine");
        Assert.Equal(["image"], again.Outputs);
        Assert.True(again.AlsoText);

        // Taken back, the two go from the file again; so does a list that was emptied.
        store.Update(s =>
        {
            s.Source("openrouter:someone/imagine").Outputs = [];
            s.Source("openrouter:someone/imagine").Inputs = [];
            s.Source("openrouter:someone/imagine").AlsoText = false;
        });
        var cleared = File.ReadAllText(path);
        Assert.DoesNotContain("outputs", cleared);
        Assert.DoesNotContain("inputs", cleared);
        Assert.DoesNotContain("alsoText", cleared);
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
        Assert.False(source.AlsoText);
    }
}
