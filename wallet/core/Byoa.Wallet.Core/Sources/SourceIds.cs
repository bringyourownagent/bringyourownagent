namespace Byoa.Wallet.Sources;

/// <summary>One canonical id function shared by the registry and OpenAI-compatible providers.</summary>
public static class SourceIds
{
    public static string OpenAiCompatible(string kind, string model, string? endpoint)
    {
        var normalizedKind = kind.Trim().ToLowerInvariant();
        var address = endpoint?.Trim().TrimEnd('/') ?? normalizedKind switch
        {
            SourceKinds.LmStudio => "http://127.0.0.1:1234/v1",
            SourceKinds.Ollama => "http://127.0.0.1:11434",
            _ => "",
        };
        if (normalizedKind == SourceKinds.Ollama && !address.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            address += "/v1";
        return normalizedKind + ":" + model.Trim() + "@" + address.ToLowerInvariant();
    }
}
