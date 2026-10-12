using System.Security.Cryptography;
using System.Text;

namespace Byoa.Wallet.Protocol;

/// <summary>Where the wallet keeps its data, where models go, and the name of its per-user pipe.</summary>
public static class WalletPaths
{
    /// <summary>Overrides the data folder (settings, secrets, activity). Used by tests to keep a separate wallet.</summary>
    public const string HomeVariable = "BYOA_WALLET_HOME";

    /// <summary>The wallet's version, reported to the extension's popup.</summary>
    public const string Version = "0.1.0";

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The data folder: <c>%LOCALAPPDATA%\BYOA\wallet</c>, or <see cref="HomeVariable"/> when set.</summary>
    public static string Home
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(HomeVariable);
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(LocalAppData, "BYOA", "wallet")
                : Path.GetFullPath(custom);
        }
    }

    /// <summary>True when <see cref="HomeVariable"/> points the wallet at a separate data folder.</summary>
    public static bool IsCustomHome => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HomeVariable));

    /// <summary>Downloaded models in the active wallet home.</summary>
    public static string Models => Path.Combine(Home, "models");

    /// <summary>
    /// The named pipe the native host uses to reach the running wallet. It is per user (the name carries a hash of
    /// the account, and both ends open it with <c>PipeOptions.CurrentUserOnly</c>) and per data folder.
    /// </summary>
    public static string PipeName(string? home = null)
    {
        var account = Environment.UserDomainName + "\\" + Environment.UserName;
        var name = "byoa-wallet-" + ShortHash(account.ToLowerInvariant());
        if (IsCustomHome || home is not null)
        {
            name += "-" + ShortHash((home ?? Home).TrimEnd('\\', '/').ToLowerInvariant());
        }

        return name;
    }

    /// <summary>The MCP management pipe, separate from the browser/native-host pipe and tied to this wallet home.</summary>
    public static string McpPipeName(string? home = null) => PipeName(home) + "-mcp";

    /// <summary>A name for the single-instance mutex, per user and data folder.</summary>
    public static string MutexName() => "Local\\" + PipeName() + "-instance";

    static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
}
