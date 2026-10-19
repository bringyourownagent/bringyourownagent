namespace Byoa.Wallet.Sources;

/// <summary>One canonical id function shared by the registry and OpenAI-compatible providers.</summary>
public static class SourceIds
{
    /// <summary>Ends the id of an added source that serves speech. A text source keeps the id it always had.</summary>
    public const string SpeechSuffix = "#transcribe";

    public static bool IsSpeech(string sourceId) => sourceId.EndsWith(SpeechSuffix, StringComparison.Ordinal);

    /// <summary>The id of a model behind an API key: the service and the model; for a service the visitor named, its address too.</summary>
    public static string Api(string service, string model, string? endpoint)
    {
        var id = ApiServices.Kind + ":" + service.Trim().ToLowerInvariant() + ":" + model.Trim();
        return service.Trim().Equals(ApiServices.Custom, StringComparison.OrdinalIgnoreCase)
            ? id + "@" + (endpoint?.Trim().TrimEnd('/').ToLowerInvariant() ?? "")
            : id;
    }

    public static string OpenAiCompatible(string kind, string model, string? endpoint, string skill = Vocabulary.Generate)
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
        var id = normalizedKind + ":" + model.Trim() + "@" + address.ToLowerInvariant();
        return skill == Vocabulary.Transcribe ? id + SpeechSuffix : id;
    }
}
