namespace Byoa.Wallet.Sources;

/// <summary>Names people see for skills and languages.</summary>
public static class Vocabulary
{
    public const string Transcribe = "audio.transcribe";
    public const string Generate = "text.generate";

    /// <summary>The skills this wallet can serve in 0.1.</summary>
    public static readonly string[] Served = [Transcribe];

    public static string SkillName(string skill) => skill switch
    {
        "audio.transcribe" => "Speech to text",
        "audio.speak" => "Read aloud",
        "text.generate" => "Text generation",
        "text.understand" => "Text understanding",
        "text.embed" => "Text embeddings",
        "image.understand" => "Image understanding",
        "image.generate" => "Image generation",
        "video.understand" => "Video understanding",
        "video.generate" => "Video generation",
        _ => skill,
    };

    static readonly Dictionary<string, string> LanguageNames = new(StringComparer.Ordinal)
    {
        ["ru"] = "Russian", ["sk"] = "Slovak", ["en"] = "English", ["uk"] = "Ukrainian", ["cs"] = "Czech",
        ["pl"] = "Polish", ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish", ["it"] = "Italian",
        ["pt"] = "Portuguese", ["nl"] = "Dutch", ["hu"] = "Hungarian", ["be"] = "Belarusian", ["zh"] = "Chinese",
        ["ja"] = "Japanese", ["ko"] = "Korean", ["tr"] = "Turkish", ["ar"] = "Arabic", ["he"] = "Hebrew",
    };

    public static string LanguageName(string code) => LanguageNames.TryGetValue(code, out var n) ? n : code;

    /// <summary>"ru-RU" → "ru"; null for anything that is not a plausible primary language tag.</summary>
    public static string? PrimaryLanguage(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var primary = tag.Trim().Split('-', '_')[0].ToLowerInvariant();
        return primary.Length is 2 or 3 && primary.All(c => c is >= 'a' and <= 'z') ? primary : null;
    }

    /// <summary>The languages Whisper models know (whisper.cpp's list).</summary>
    public static readonly string[] WhisperLanguages =
    [
        "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca", "nl", "ar", "sv", "it", "id", "hi", "fi", "vi",
        "he", "uk", "el", "ms", "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la", "mi", "ml", "cy", "sk",
        "te", "fa", "lv", "bn", "sr", "az", "sl", "kn", "et", "mk", "br", "eu", "is", "hy", "ne", "mn", "bs", "kk", "sq", "sw",
        "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be", "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo",
        "ht", "ps", "tk", "nn", "mt", "sa", "lb", "my", "bo", "tl", "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw", "su", "yue",
    ];
}
