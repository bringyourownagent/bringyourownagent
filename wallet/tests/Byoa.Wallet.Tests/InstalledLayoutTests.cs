using Byoa.Wallet.Desktop;
using Byoa.Wallet.Protocol;
using Xunit;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The two facts of an installed wallet that the unit tests of earlier rounds hid by setting test-only variables:
/// the wallet recognises its own bridge when its folder is spelled the way <c>AppContext.BaseDirectory</c> spells it
/// (with a trailing backslash), and a visitor's models from an older build are found where that build put them.
/// </summary>
public class InstalledLayoutTests
{
    [Theory]
    [InlineData(@"C:\Users\v\AppData\Local\BYOA\wallet-app\", @"C:\Users\v\AppData\Local\BYOA\wallet-app\byoa-mcp.exe", true)]
    [InlineData(@"C:\Users\v\AppData\Local\BYOA\wallet-app", @"C:\Users\v\AppData\Local\BYOA\wallet-app\byoa-mcp.exe", true)]
    [InlineData(@"C:\Users\v\AppData\Local\BYOA\wallet-app\", @"c:\users\v\appdata\local\byoa\WALLET-APP\BYOA-MCP.EXE", true)]
    [InlineData(@"C:\Users\v\AppData\Local\BYOA\wallet-app\Byoa.Wallet.exe", @"C:\Users\v\AppData\Local\BYOA\wallet-app\byoa-mcp.exe", true)]
    [InlineData(@"C:\Users\v\AppData\Local\BYOA\wallet-app\", @"C:\Users\v\AppData\Local\BYOA\wallet-app2\byoa-mcp.exe", false)]
    [InlineData(@"C:\Users\v\AppData\Local\BYOA\wallet-app\", @"C:\Users\v\AppData\Local\BYOA\wallet-app\sub\byoa-mcp.exe", false)]
    [InlineData(@"C:\Users\v\AppData\Local\BYOA\wallet-app\", @"C:\Temp\byoa-mcp.exe", false)]
    [InlineData(@"C:\Users\v\AppData\Local\BYOA\wallet-app\", @"C:\Users\v\AppData\Local\BYOA\wallet-app\other.exe", false)]
    [InlineData(@"C:\Users\v\AppData\Local\BYOA\wallet-app\", @"byoa-mcp.exe", false)]
    public void TheWalletRecognisesItsOwnBridgeOnlyInItsOwnFolder(string walletFolder, string clientImage, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, AgentPipeServer.IsBridgeIn(clientImage, walletFolder));
    }

    [Fact]
    public void TheFolderOfTheRunningWalletIsAcceptedAsAppContextSpellsIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        // AppContext.BaseDirectory always ends in a separator; this is the string the installed wallet compares with.
        var folder = AppContext.BaseDirectory;
        Assert.True(folder.EndsWith(Path.DirectorySeparatorChar) || folder.EndsWith(Path.AltDirectorySeparatorChar));
        Assert.True(AgentPipeServer.IsBridgeIn(Path.Combine(folder, "byoa-mcp.exe"), folder));
    }

    static string Fresh(string name) => Path.Combine(AppContext.BaseDirectory, "layout-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void ModelsOfAnOlderBuildAreUsedWhereTheyAre()
    {
        var local = Fresh("local");
        var home = Path.Combine(local, "BYOA", "wallet");
        var legacy = Path.Combine(local, "BYOA", "models");
        Directory.CreateDirectory(Path.Combine(legacy, "whisper"));
        File.WriteAllText(Path.Combine(legacy, "whisper", "ggml-large-v3-q5_0.bin"), "x");
        Directory.CreateDirectory(home);

        Assert.Equal(legacy, WalletPaths.ModelsFor(home, local, customHome: false));
    }

    [Fact]
    public void ModelsAreInTheWalletHomeWhenThereIsNothingOlderOrTheHomeAlreadyHasThem()
    {
        var local = Fresh("local");
        var home = Path.Combine(local, "BYOA", "wallet");
        var current = Path.Combine(home, "models");
        var legacy = Path.Combine(local, "BYOA", "models");

        // nothing anywhere: the new place
        Assert.Equal(current, WalletPaths.ModelsFor(home, local, customHome: false));

        // an older folder exists, but the home already holds models: the home wins (the visitor chose to download there)
        Directory.CreateDirectory(Path.Combine(legacy, "whisper"));
        Directory.CreateDirectory(Path.Combine(current, "whisper"));
        File.WriteAllText(Path.Combine(current, "whisper", "ggml-small-q5_1.bin"), "x");
        Assert.Equal(current, WalletPaths.ModelsFor(home, local, customHome: false));
    }

    [Fact]
    public void ASeparateDataFolderNeverReachesIntoTheVisitorsModels()
    {
        var local = Fresh("local");
        var home = Fresh("test-home");
        Directory.CreateDirectory(Path.Combine(local, "BYOA", "models", "whisper"));

        Assert.Equal(Path.Combine(home, "models"), WalletPaths.ModelsFor(home, local, customHome: true));
    }
}
