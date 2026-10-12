using System.Reflection;
using System.Text;

namespace Byoa.Wallet.Help;

/// <summary>
/// The one source of help for the desktop window and the MCP agent. Articles are embedded markdown so an installed
/// wallet never needs to read a help file from a user-controlled path.
/// </summary>
public static class HelpCatalog
{
    const string Prefix = "Byoa.Wallet.Help.";
    const string Suffix = ".md";
    static readonly Assembly Assembly = typeof(HelpCatalog).Assembly;
    static readonly IReadOnlyList<string> TopicList = Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Suffix, StringComparison.Ordinal))
        .Select(n => n[Prefix.Length..^Suffix.Length])
        .Where(n => n.Length > 0)
        .OrderBy(n => n == "overview" ? "" : n, StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<string> Topics => TopicList;

    public static string Get(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return Get("overview");
        var key = topic.Trim().ToLowerInvariant();
        if (!TopicList.Contains(key, StringComparer.Ordinal))
            throw new KeyNotFoundException("There is no help topic named '" + topic.Trim() + "'. Available topics: " + string.Join(", ", TopicList) + ".");

        var name = Prefix + key + Suffix;
        using var stream = Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The embedded help article is missing: " + key);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Trim() + Environment.NewLine;
    }

    /// <summary>Overview text for MCP: it includes the topic list and the complete add-a-model route.</summary>
    public static string OverviewForAgent()
    {
        var overview = Get("overview").TrimEnd();
        return overview + Environment.NewLine + Environment.NewLine
            + "Topics: " + string.Join(", ", TopicList) + Environment.NewLine
            + "To add a local model: list_runtimes, choose a running model, call add_source with its kind/model/endpoint, "
            + "test_source, then set_order. The wallet asks the visitor for an API key in its own window; prefer "
            + "request_api_key_entry over set_api_key. The wallet never returns a key." + Environment.NewLine;
    }
}
