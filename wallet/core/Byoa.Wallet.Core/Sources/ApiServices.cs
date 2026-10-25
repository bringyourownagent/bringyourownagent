namespace Byoa.Wallet.Sources;

/// <summary>
/// A company that sells models behind an API key and speaks the OpenAI chat protocol. The visitor adds a key per service; every model
/// he adds behind it uses that key. OpenRouter is the one aggregator and has a kind of its own (<see cref="SourceKinds.OpenRouter"/>).
/// </summary>
/// <param name="Id">The id kept in the settings ("openai").</param>
/// <param name="BaseUrl">The OpenAI-style base (the wallet appends <c>/chat/completions</c> and <c>/models</c>).</param>
/// <param name="SecretName">The name of its key in the wallet's secret store.</param>
/// <param name="Vendor">The vendor part of the names of its models, as <see cref="ModelNames"/> spells it ("x-ai").</param>
/// <param name="KeyPage">Where the visitor makes a key.</param>
public sealed record ApiService(string Id, string Name, string BaseUrl, string SecretName, string? Vendor, string KeyPage);

public static class ApiServices
{
    /// <summary>The kind of an added source that sits behind one of these services (or another OpenAI-style service the visitor names).</summary>
    public const string Kind = "api";

    /// <summary>The service whose address the visitor types himself.</summary>
    public const string Custom = "custom";

    public static readonly IReadOnlyList<ApiService> All =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", "openai", "openai", "https://platform.openai.com/api-keys"),
        new("anthropic", "Anthropic", "https://api.anthropic.com/v1", "anthropic", "anthropic", "https://console.anthropic.com/settings/keys"),
        new("google", "Google AI Studio", "https://generativelanguage.googleapis.com/v1beta/openai", "google", "google", "https://aistudio.google.com/apikey"),
        new("minimax", "MiniMax", "https://api.minimax.io/v1", "minimax", "minimax", "https://platform.minimax.io"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek", "deepseek", "https://platform.deepseek.com/api_keys"),
        new("xai", "xAI", "https://api.x.ai/v1", "xai", "x-ai", "https://console.x.ai"),
        new("zai", "Z.ai (GLM)", "https://api.z.ai/api/paas/v4", "zai", "z-ai", "https://z.ai/manage-apikey/apikey-list"),
        // The same company, the same key, another address: a Coding Plan subscription is used at /api/coding/paas/v4, the pay-per-token account at /api/paas/v4.
        new("zai-coding", "Z.ai Coding Plan (GLM)", "https://api.z.ai/api/coding/paas/v4", "zai", "z-ai", "https://z.ai/manage-apikey/apikey-list"),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral", "mistralai", "https://console.mistral.ai/api-keys"),
    ];

    /// <summary>
    /// The name of the key kept for a service: its own (the service's <see cref="ApiService.SecretName"/>, which two addresses of one company share);
    /// for a service the visitor named, its address too (two such services have two keys). Every model behind one service uses the one key.
    /// </summary>
    public static string SecretName(string? service, string? endpoint)
    {
        var id = (service ?? "").Trim().ToLowerInvariant();
        if (id != Custom) return "api-key:" + (Find(id)?.SecretName ?? id);
        return Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) ? "api-key:custom:" + uri.IdnHost.ToLowerInvariant() : "api-key:custom";
    }

    public static ApiService? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(s => s.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>"OpenAI", or "your own service" for a custom one.</summary>
    public static string NameOf(string? id) => Find(id)?.Name ?? (id == Custom ? "Other service" : "API");
}
