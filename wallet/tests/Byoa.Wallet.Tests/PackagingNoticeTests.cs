using Byoa.Wallet.Providers;

namespace Byoa.Wallet.Tests;

/// <summary>The license notices that ship with the package must agree with what the wallet downloads, and with the
/// license files at the root of the repository.</summary>
public sealed class PackagingNoticeTests
{
    // The repository root is found by walking up from the test binaries, as the other tests that read repository files do.
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "wallet", "packaging", "THIRD-PARTY-NOTICES.txt"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    [Fact]
    public void TheNoticesNameTheGigaAmModelAndTheLicenseTheWalletDownloads()
    {
        var notices = File.ReadAllText(Path.Combine(RepoRoot(), "wallet", "packaging", "THIRD-PARTY-NOTICES.txt"));
        var license = GigaAmSource.DownloadFiles.Single(file => file.Name == "LICENSE");

        Assert.Contains(GigaAmSource.ModelFolderName, notices, StringComparison.Ordinal);
        Assert.Contains(license.Sha256, notices, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2024 GigaChat Team", notices, StringComparison.Ordinal);
        Assert.DoesNotContain("only from a folder", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLicenseNamesTheHolderAndLeavesNoTemplate()
    {
        const string holder = "Copyright 2026 ultrathinker and the Bring Your Own Agent contributors";
        var root = RepoRoot();
        var license = File.ReadAllText(Path.Combine(root, "LICENSE"));

        Assert.Contains(holder, license, StringComparison.Ordinal);
        Assert.DoesNotContain("[yyyy]", license, StringComparison.Ordinal);
        Assert.DoesNotContain("[name of copyright owner]", license, StringComparison.Ordinal);
        Assert.Contains(holder, File.ReadAllText(Path.Combine(root, "NOTICE")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedNoticeFilesAreAscii()
    {
        // make-package.ps1 (Write-Text) refuses characters outside ASCII in these files.
        var root = RepoRoot();
        foreach (var path in new[] { Path.Combine(root, "LICENSE"), Path.Combine(root, "NOTICE"), Path.Combine(root, "wallet", "packaging", "THIRD-PARTY-NOTICES.txt") })
        {
            Assert.All(File.ReadAllBytes(path), b => Assert.True(b < 0x80, $"{path} has a non-ASCII byte"));
        }
    }
}
