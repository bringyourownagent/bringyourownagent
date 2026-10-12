using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;
using System.Net.Http.Json;

namespace Byoa.Wallet.Desktop;

/// <summary>Tests local built-ins with a tiny sample. Paid sources are never called silently by a test button.</summary>
sealed class WalletSourceTester : IRegistrySourceTester
{
    readonly IReadOnlyList<ISpeechSource> _speech;
    readonly HttpClient _http;
    readonly SettingsStore? _settings;

    public WalletSourceTester(IEnumerable<ISpeechSource> speech, HttpClient http, SettingsStore? settings = null)
    {
        _speech = speech.ToArray();
        _http = http;
        _settings = settings;
    }

    public async Task<RegistryTestResult> TestSourceAsync(string sourceId, CancellationToken ct)
    {
        var source = _speech.FirstOrDefault(s => s.Id == sourceId);
        if (source is not null)
        {
            if (source.Paid) return new RegistryTestResult(false, "This is a paid source. The wallet will not make a paid test call without a separate visitor confirmation.");
            try
            {
                var answer = await source.TranscribeAsync(Sample(), "ru", ct).ConfigureAwait(false);
                return new RegistryTestResult(true, "Test passed: the source answered a short local sample.", answer.Detail);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                return new RegistryTestResult(false, "Test failed: " + Short(e), e.GetType().Name);
            }
        }

        var added = _settings?.Snapshot().Sources.FirstOrDefault(s => s.Id == sourceId && s.Kind is not null);
        return added is null
            ? new RegistryTestResult(false, "That source is not available to the current wallet engine yet. Add it after the text provider is installed.")
            : await TestDraftAsync(added, ct).ConfigureAwait(false);
    }

    public async Task<RegistryTestResult> TestDraftAsync(SourceSetting draft, CancellationToken ct)
    {
        if (draft.Kind == SourceKinds.OpenRouter)
            return new RegistryTestResult(false, "OpenRouter is paid. The wallet will not make a paid test call automatically.");
        if (string.IsNullOrWhiteSpace(draft.Endpoint) || string.IsNullOrWhiteSpace(draft.Model))
            return new RegistryTestResult(false, "Give the local source an endpoint and model id first.");
        if (!draft.Skills.Contains(Vocabulary.Generate, StringComparer.Ordinal))
            return new RegistryTestResult(false, "The current local test supports text.generate sources; choose that skill first.");

        try
        {
            var endpoint = draft.Endpoint.TrimEnd('/');
            var body = new
            {
                model = draft.Model,
                messages = new[] { new { role = "user", content = "Reply with exactly: wallet test ok" } },
                max_tokens = 16,
                temperature = 0,
            };
            using var response = await _http.PostAsJsonAsync(endpoint + "/chat/completions", body, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new RegistryTestResult(false, "Test failed: the model server returned HTTP " + (int)response.StatusCode + ". Start it or check the endpoint.");
            return new RegistryTestResult(true, "Test passed: the local model answered the one-line prompt.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new RegistryTestResult(false, "Test failed: the local model server could not be reached. Start it or check the endpoint.");
        }
    }

    static float[] Sample()
    {
        var samples = new float[1600];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.03f * MathF.Sin(i * 0.05f);
        return samples;
    }

    static string Short(Exception e) => e.Message.Length > 160 ? e.Message[..157] + "..." : e.Message;
}
