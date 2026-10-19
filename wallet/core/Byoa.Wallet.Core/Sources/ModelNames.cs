using System.Text;
using System.Text.RegularExpressions;

namespace Byoa.Wallet.Sources;

/// <summary>
/// Names of models, so that a site that asks for "openai/gpt-5*" or "google/gemma-3-12b-it" finds what the visitor has, whichever
/// road it comes by (a key at OpenRouter, a key at the vendor, a program on this PC under its own file name). A name is
/// <c>vendor/model</c> in the style OpenRouter made common; the vendor may be missing (a local model whose maker the wallet cannot
/// tell), and then only the model part is compared.
/// </summary>
public static partial class ModelNames
{
    /// <summary>The one spelling two names are compared by: lower case, no variant after a colon, dots and underscores as hyphens, no date.</summary>
    public static string Canon(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var text = name.Trim().ToLowerInvariant();
        var colon = text.IndexOf(':');
        if (colon > 0) text = text[..colon];                    // ":free", ":thinking", ":nitro": the same model on another road
        text = text.Replace('_', '-').Replace('.', '-').Replace(' ', '-');
        text = DateSuffix().Replace(text, "");
        text = RepeatedHyphen().Replace(text, "-");
        return text.Trim('-');
    }

    /// <summary>The vendor part of a name, as the wallet spells vendors ("x-ai"), or null when the name has none.</summary>
    public static string? VendorPart(string? name)
    {
        var text = Canon(name?.Replace("*", "", StringComparison.Ordinal));
        var slash = text.IndexOf('/');
        return slash > 0 ? NormalVendor(text[..slash]) : null;
    }

    /// <summary>The model part of a name: after the vendor, up to a mask's star.</summary>
    public static string ModelPart(string? name)
    {
        var text = Canon(name);
        var slash = text.LastIndexOf('/');
        return slash >= 0 ? text[(slash + 1)..] : text;
    }

    /// <summary>
    /// Whether a model name (the site's pattern: a name, or a mask ending in <c>*</c>) names this model. A pattern with a vendor
    /// needs the same vendor when the model's vendor is known; without a vendor on either side only the model part is compared.
    /// </summary>
    public static bool Matches(string? pattern, string? canonical)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(canonical)) return false;
        var raw = pattern.Trim();
        var star = raw.EndsWith('*');
        if (star) raw = raw[..^1];
        var patternVendor = VendorPart(raw);
        var patternTail = ModelPart(raw);
        var vendor = VendorPart(canonical);
        var tail = ModelPart(canonical);
        // A vendor the wallet knows that differs from the site's is a different model; an unknown maker of a file (ggml-org, unsloth) proves nothing.
        if (patternVendor is not null && vendor is not null && patternVendor != vendor && KnownVendors.Contains(vendor)) return false;
        return star ? tail.StartsWith(patternTail, StringComparison.Ordinal) : tail == patternTail;
    }

    /// <summary>The company behind a model: its vendor part, or the one its family is known by ("gemma" is Google's), or the model itself.</summary>
    public static string Vendor(string? canonical)
    {
        if (VendorPart(canonical) is { } vendor && KnownVendors.Contains(vendor)) return vendor;
        var tail = ModelPart(canonical);
        foreach (var (prefix, owner) in Families)
        {
            if (tail.StartsWith(prefix, StringComparison.Ordinal)) return owner;
        }

        return VendorPart(canonical) ?? "other:" + tail;
    }

    /// <summary>
    /// The name a model added from a local program goes by when the visitor gave none: the program's id without the maker of the file
    /// (ggml-org, unsloth...), the GGUF tag and the quantisation. "ggml-org/gemma-3-1b-it-GGUF:Q4_K_M" is "gemma-3-1b-it".
    /// </summary>
    public static string FromLocal(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "";
        var text = model.Trim();
        var colon = text.IndexOf(':');
        var variant = colon > 0 ? text[(colon + 1)..] : null;
        if (colon > 0) text = text[..colon];
        text = text.ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        text = text.Replace(".gguf", "", StringComparison.Ordinal);
        string? vendor = null;
        var slash = text.LastIndexOf('/');
        if (slash >= 0)
        {
            var org = NormalVendor(text[..slash]);
            if (KnownVendors.Contains(org)) vendor = org;
            text = text[(slash + 1)..];
        }

        text = GgufTag().Replace(text, "");
        text = QuantSuffix().Replace(text, "");
        // Ollama's tag after the colon is a size ("3b"), not a quantisation: it belongs to the name.
        if (variant is { Length: > 0 } && SizeTag().IsMatch(variant)) text += "-" + variant.ToLowerInvariant();
        var canonical = Canon(text);
        return vendor is null ? canonical : vendor + "/" + canonical;
    }

    /// <summary>A name people read: "gemma-3-12b-it" as "Gemma 3 12B It" is noisy, so the wallet keeps the catalog's own name when it has one.</summary>
    public static string Display(string? model, string? catalogName = null)
    {
        if (!string.IsNullOrWhiteSpace(catalogName))
        {
            var name = catalogName.Trim();
            var colon = name.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && colon < 24) name = name[(colon + 2)..];      // "Google: Gemma 3 12B" → "Gemma 3 12B"
            name = Regex.Replace(name, @"\s*\((free|paid)\)$", "", RegexOptions.IgnoreCase);
            if (name.Length > 0) return name;
        }

        var text = (model ?? "").Trim();
        var slash = text.LastIndexOf('/');
        if (slash >= 0) text = text[(slash + 1)..];
        var tag = text.IndexOf(':');
        if (tag > 0) text = text[..tag];
        text = GgufTag().Replace(text, "");
        return text.Length == 0 ? "model" : text;
    }

    static string NormalVendor(string vendor)
    {
        var v = Canon(vendor);
        return v switch
        {
            "xai" => "x-ai",
            "zai" or "z" => "z-ai",
            "mistral" => "mistralai",
            "meta" or "metaai" or "meta-ai" => "meta-llama",
            "moonshot" => "moonshotai",
            "gemini" or "google-ai" => "google",
            _ => v,
        };
    }

    public static readonly IReadOnlySet<string> KnownVendors = new HashSet<string>(StringComparer.Ordinal)
    {
        "anthropic", "openai", "google", "meta-llama", "mistralai", "deepseek", "qwen", "x-ai", "z-ai", "minimax", "amazon",
        "microsoft", "nvidia", "cohere", "ai21", "moonshotai", "perplexity", "nousresearch", "inception", "tencent", "baidu",
        "bytedance", "stepfun", "ibm-granite", "allenai", "liquid", "thudm",
    };

    static readonly (string Prefix, string Owner)[] Families =
    [
        ("gemma", "google"), ("gemini", "google"), ("llama", "meta-llama"), ("qwen", "qwen"), ("qwq", "qwen"),
        ("mistral", "mistralai"), ("mixtral", "mistralai"), ("ministral", "mistralai"), ("codestral", "mistralai"),
        ("phi", "microsoft"), ("deepseek", "deepseek"), ("gpt", "openai"), ("o1", "openai"), ("o3", "openai"), ("o4", "openai"),
        ("claude", "anthropic"), ("grok", "x-ai"), ("glm", "z-ai"), ("minimax", "minimax"), ("nova", "amazon"),
        ("kimi", "moonshotai"), ("command", "cohere"), ("sonar", "perplexity"), ("nemotron", "nvidia"),
    ];

    [GeneratedRegex(@"-(\d{8}|\d{4}-\d{2}-\d{2})$")]
    private static partial Regex DateSuffix();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedHyphen();

    [GeneratedRegex("-gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex GgufTag();

    [GeneratedRegex(@"-(q\d(-\d|-k(-[msl])?)?|iq\d(-[a-z]+)?|f16|bf16|f32|fp16|fp8|int[48])$")]
    private static partial Regex QuantSuffix();

    [GeneratedRegex(@"^\d+(\.\d+)?[bm]$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeTag();
}
