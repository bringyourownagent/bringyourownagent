using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Byoa.Wallet.Help;

namespace Byoa.Wallet.Android;

/// <summary>
/// Help: the wallet's own short articles (the same ones the desktop wallet has, embedded in the core), each opening on a page of its own. The
/// articles about what only the desktop wallet does (Everywhere, agents over MCP, the guard) are not offered here; they are named at the end,
/// so that nobody looks for them.
/// </summary>
sealed class HelpPage : ListPage
{
    /// <summary>The articles about things this wallet does not do on a phone.</summary>
    static readonly string[] DesktopOnly = ["everywhere", "agents", "your-agent", "guard"];

    public HelpPage(Shell shell) : base(shell)
    {
    }

    public override string? Title => "Help";

    protected override void Fill()
    {
        Body.Children.Add(Kit.Text("Short articles for people who are not developers. They are the desktop wallet's articles: where one says “this PC” or “the window”, the same is true on the phone.", "muted"));
        foreach (var topic in HelpCatalog.Topics.Where(t => !DesktopOnly.Contains(t)))
        {
            var name = HelpCatalog.Title(topic);
            var preview = HelpArticle.Preview(HelpCatalog.Get(topic));
            var text = Kit.Column(3, Kit.Text(name, "h2"), Kit.Text(preview, "muted", "small"));
            var read = Kit.Button("Read", "deny", "HelpRead-" + topic, () => Shell.Push(new HelpArticlePage(Shell, topic)), "small");
            var card = Kit.Box("row", Kit.Columns("*,Auto", text, read));
            AutomationProperties.SetName(card, name);
            Body.Children.Add(card);
        }

        var left = HelpCatalog.Topics.Where(DesktopOnly.Contains).Select(HelpCatalog.Title).ToList();
        if (left.Count > 0)
        {
            Body.Children.Add(Kit.Text("Desktop only", "label"));
            Body.Children.Add(Kit.Text("These articles are about things only the desktop wallet does, so they are not here: " + string.Join("; ", left) + ".", "muted", "small"));
        }
    }
}

/// <summary>One help article, laid out to be read on a phone: its headings, its paragraphs and its lists.</summary>
sealed class HelpArticlePage : ListPage
{
    readonly string _topic;

    public HelpArticlePage(Shell shell, string topic) : base(shell) => _topic = topic;

    public override string? Title => HelpCatalog.Title(_topic);

    protected override void Fill()
    {
        Body.Spacing = 10;
        foreach (var block in HelpArticle.Blocks(HelpCatalog.Get(_topic))) Body.Children.Add(block);
    }
}

/// <summary>The little of Markdown the articles use: <c>#</c> and <c>##</c> headings, <c>-</c> lists, paragraphs, and `code` and **bold** inside a line.</summary>
static class HelpArticle
{
    /// <summary>The first line of text of an article, without its marks (the grey line under its name in the list).</summary>
    public static string Preview(string markdown) =>
        Clean((markdown.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "").TrimStart('-', ' '));

    public static IEnumerable<Control> Blocks(string markdown)
    {
        var paragraph = new StringBuilder();

        Control? Flush()
        {
            if (paragraph.Length == 0) return null;
            var text = paragraph.ToString().Trim();
            paragraph.Clear();
            return Kit.Text(Clean(text));
        }

        foreach (var raw in markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (Flush() is { } done) yield return done;
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (Flush() is { } before) yield return before;
                var heading = Kit.Text(Clean(line.TrimStart('#', ' ')), line.StartsWith("##", StringComparison.Ordinal) ? "h2" : "h1");
                heading.Margin = new Thickness(0, 6, 0, 0);
                yield return heading;
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                if (Flush() is { } before) yield return before;
                var item = Kit.Columns("Auto,*", Kit.Text("•"), Kit.Text(Clean(line[2..])));
                ((Control)item.Children[1]).Margin = new Thickness(8, 0, 0, 0);
                yield return item;
                continue;
            }

            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line.Trim());
        }

        if (Flush() is { } last) yield return last;
    }

    /// <summary>A line without the marks of code and bold (a phone shows plain words).</summary>
    static string Clean(string line) => line.Replace("`", "", StringComparison.Ordinal).Replace("**", "", StringComparison.Ordinal);
}
