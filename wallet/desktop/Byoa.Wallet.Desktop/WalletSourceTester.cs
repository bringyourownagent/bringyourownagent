using System.Text.Json.Nodes;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>Adapts the engine's wallet-owned test call to the registry's plain result type.</summary>
sealed class WalletSourceTester : IRegistrySourceTester, IVisitorInitiatedSourceTester
{
    readonly WalletEngine _engine;
    readonly SourceSet _sources;
    readonly SettingsStore _settings;

    public WalletSourceTester(WalletEngine engine, SourceSet sources, SettingsStore settings)
    {
        _engine = engine;
        _sources = sources;
        _settings = settings;
    }

    public Task<RegistryTestResult> TestSourceAsync(string sourceId, CancellationToken ct) =>
        TestSourceAsync(sourceId, visitorInitiated: false, ct);

    public async Task<RegistryTestResult> TestSourceAsync(string sourceId, bool visitorInitiated, CancellationToken ct)
    {
        var source = _sources.All.FirstOrDefault(s => s.Id == sourceId);
        if (source is not null) return ToResult(await _engine.TestSourceAsync(source.Id, Vocabulary.Transcribe, visitorInitiated, ct).ConfigureAwait(false));

        var setting = _settings.Snapshot().Sources.FirstOrDefault(s => s.Id == sourceId && s.Kind is not null);
        var text = _sources.FindText(sourceId) ?? (setting is null ? null : _sources.CreateTextSource(setting));
        if (text is null) return new RegistryTestResult(false, "There is no source with id '" + sourceId + "' in the running wallet.");
        return ToResult(await _engine.TestSourceAsync(text, Vocabulary.Generate, visitorInitiated, ct).ConfigureAwait(false));
    }

    public Task<RegistryTestResult> TestDraftAsync(SourceSetting draft, CancellationToken ct) =>
        TestDraftAsync(draft, visitorInitiated: false, ct);

    public async Task<RegistryTestResult> TestDraftAsync(SourceSetting draft, bool visitorInitiated, CancellationToken ct)
    {
        var skill = draft.Skills.Contains(Vocabulary.Transcribe, StringComparer.Ordinal)
            ? Vocabulary.Transcribe : Vocabulary.Generate;
        if (skill == Vocabulary.Transcribe)
            return new RegistryTestResult(false, "Added model sources currently serve text.generate; choose Text generation.");
        var text = _sources.CreateTextSource(draft);
        if (text is null) return new RegistryTestResult(false, "Give the local source an endpoint and model id first.");
        return ToResult(await _engine.TestSourceAsync(text, skill, visitorInitiated, ct).ConfigureAwait(false));
    }

    static RegistryTestResult ToResult(JsonNode? result)
    {
        if (result is JsonObject { } json && json["ok"]?.GetValue<bool>() == true)
            return new RegistryTestResult(true, "Test passed: the wallet routed the short sample through this source.",
                json["output"]?["text"]?.GetValue<string>());
        var reason = result is JsonObject failed && failed["error"]?.GetValue<string>() is { } error
            ? error : "the source did not answer";
        return new RegistryTestResult(false, "Test failed: " + reason + ".");
    }
}
