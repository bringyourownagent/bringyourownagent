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

    [Fact]
    public void InstalledBridgeCheckUsesAppContextWhenNoTestOverrideIsPresent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var old = Environment.GetEnvironmentVariable("BYOA_WALLET_MCP_PATH");
        Environment.SetEnvironmentVariable("BYOA_WALLET_MCP_PATH", null);
        try
        {
            Assert.True(AgentPipeServer.IsInstalledBridge(Path.Combine(AppContext.BaseDirectory, "byoa-mcp.exe")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BYOA_WALLET_MCP_PATH", old);
        }
    }
}
