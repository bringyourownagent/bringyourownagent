using Byoa.Wallet.Desktop;
using Byoa.Wallet.Engine;

namespace Byoa.Wallet.Tests;

public sealed class DesktopPolishTests
{
    [Fact]
    public void ALinksNoticeSaysNotNowAndSendsBlockToSites()
    {
        Assert.Equal(("Not now", "Block in Sites…"), ToastWindow.Choices(new NoticeModel("https://geo.example.org", "geo.example.org", FromLink: true)));
        Assert.Equal(("Decline", "Block this site"), ToastWindow.Choices(new NoticeModel("https://geo.example.org", "geo.example.org")));
    }

    [Fact]
    public void AgentCommandsKeepDoubleHyphensAndJsonEscapesOnlyTheJsonPath()
    {
        var executable = @"C:\Program Files\BYOA\byoa-mcp.exe";
        var snippets = SettingsWindow.AgentSnippets(executable).ToDictionary(item => item.Name, item => item.Text);

        Assert.Equal("claude mcp add --transport stdio byoa-wallet -- \"C:\\Program Files\\BYOA\\byoa-mcp.exe\"", snippets["Claude Code"]);
        Assert.Equal("codex mcp add byoa-wallet -- \"C:\\Program Files\\BYOA\\byoa-mcp.exe\"", snippets["Codex"]);
        Assert.Contains("\"type\": \"local\"", snippets["OpenCode"], StringComparison.Ordinal);
        Assert.Contains("C:\\\\Program Files\\\\BYOA\\\\byoa-mcp.exe", snippets["OpenCode"], StringComparison.Ordinal);
        Assert.DoesNotContain("—", snippets["Claude Code"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(800, 360, 680)]
    [InlineData(900, 360, 765)]
    [InlineData(1200, 360, 1020)]
    public void PromptMaximumHeightLeavesTheWorkingAreaMargin(double workingHeight, double minimum, double expected)
    {
        Assert.Equal(expected, WindowLayout.CalculateMaxHeight(workingHeight, minimum), precision: 6);
    }

    [Fact]
    public void SourceAndEmbeddedTextHasNoCommonMojibake()
    {
        var mojibakeEuro = string.Concat('\u00e2', '\u20ac');
        var mojibakeArrow = string.Concat('\u00e2', '\u2020');
        var mojibakeLatin = "\u00c3";
        var root = FindRoot();
        var paths = Directory.EnumerateFiles(Path.Combine(root, "wallet"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*", SearchOption.AllDirectories))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetExtension(path) is ".cs" or ".axaml" or ".md" or ".json" or ".ps1")
            .ToArray();

        foreach (var path in paths)
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain(mojibakeEuro, text, StringComparison.Ordinal);
            Assert.DoesNotContain(mojibakeArrow, text, StringComparison.Ordinal);
            Assert.DoesNotContain(mojibakeLatin, text, StringComparison.Ordinal);
        }

        foreach (var topic in Byoa.Wallet.Help.HelpCatalog.Topics)
        {
            var article = Byoa.Wallet.Help.HelpCatalog.Get(topic);
            Assert.DoesNotContain(mojibakeEuro, article, StringComparison.Ordinal);
            Assert.DoesNotContain(mojibakeArrow, article, StringComparison.Ordinal);
            Assert.DoesNotContain(mojibakeLatin, article, StringComparison.Ordinal);
        }
    }

    static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "wallet", "Byoa.Wallet.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the wallet repository root from the test output folder.");
    }
}
