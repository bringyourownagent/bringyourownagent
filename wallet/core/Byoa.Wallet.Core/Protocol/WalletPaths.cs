using System.Security.Cryptography;
using System.Text;

namespace Byoa.Wallet.Protocol;

/// <summary>Where the wallet keeps its data, where models go, and the name of its per-user pipe.</summary>
public static class WalletPaths
{
    /// <summary>Overrides the data folder (settings, secrets, activity). Used by tests to keep a separate wallet.</summary>
    public const string HomeVariable = "BYOA_WALLET_HOME";

    /// <summary>
    /// The wallet's version, reported to the extension's popup and compared by the updater: the one number in <c>Directory.Build.props</c>
    /// that every program of the wallet is built with (a build's commit mark after "+" is left out).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    static string ReadVersion()
    {
        var text = typeof(WalletPaths).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(text)) return typeof(WalletPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? text[..plus] : text;
    }

    /// <summary>
    /// Where per-user application data lives: <c>%LOCALAPPDATA%</c> on Windows, <c>~/Library/Application Support</c> on macOS (.NET would
    /// say <c>~/.local/share</c> there, which is the Linux place).
    /// </summary>
    static string DataRoot => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The data folder: <c>%LOCALAPPDATA%\BYOA\wallet</c> (macOS: <c>~/Library/Application Support/BYOA/wallet</c>), or <see cref="HomeVariable"/> when set.</summary>
    public static string Home
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(HomeVariable);
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(DataRoot, "BYOA", "wallet")
                : Path.GetFullPath(custom);
        }
    }

    /// <summary>True when <see cref="HomeVariable"/> points the wallet at a separate data folder.</summary>
    public static bool IsCustomHome => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HomeVariable));

    /// <summary>
    /// The pipe the native host uses to reach the running wallet. On Windows it is a named pipe, per user (the name carries a hash of
    /// the account) and per data folder. On macOS and Linux .NET makes a pipe a Unix socket, and the name is the full path of its file:
    /// in a folder of the user's own (<see cref="RunFolder"/>), never under TMPDIR, because an agent's sandbox may give a program another
    /// TMPDIR and then the MCP bridge would look for the wallet in the wrong place. Both ends open it with <c>PipeOptions.CurrentUserOnly</c>.
    /// </summary>
    public static string PipeName(string? home = null)
    {
        if (!OperatingSystem.IsWindows()) return SocketPath("wallet", home);
        var account = Environment.UserDomainName + "\\" + Environment.UserName;
        var name = "byoa-wallet-" + ShortHash(account.ToLowerInvariant());
        if (IsCustomHome || home is not null)
        {
            name += "-" + ShortHash((home ?? Home).TrimEnd('\\', '/').ToLowerInvariant());
        }

        return name;
    }

    /// <summary>The MCP management pipe, separate from the browser/native-host pipe and tied to this wallet home.</summary>
    public static string McpPipeName(string? home = null) => OperatingSystem.IsWindows() ? PipeName(home) + "-mcp" : SocketPath("mcp", home);

    /// <summary>A name for the single-instance mutex, per user and data folder.</summary>
    public static string MutexName() => "Local\\" + PipeName() + "-instance";

    /// <summary>The file that keeps a second wallet from running on the same data folder (macOS and Linux; Windows uses a mutex).</summary>
    public static string InstanceLockPath => Path.Combine(Home, "instance.lock");

    /// <summary>Where the wallet's sockets are (macOS and Linux): a folder only this user can open.</summary>
    public static string RunFolder => Path.Combine(DataRoot, "BYOA", "run");

    /// <summary>
    /// Makes sure the folder of a socket exists and is closed to other users. Called by the wallet before it listens; it does nothing on
    /// Windows, where pipes have no folder. A socket path is limited to 103 characters, which this reports as a clear error instead of
    /// the runtime's.
    /// </summary>
    public static void PrepareSocketFolder(string pipeName)
    {
        if (OperatingSystem.IsWindows() || !Path.IsPathRooted(pipeName)) return;
        if (pipeName.Length > 103) throw new IOException("The wallet's socket path is too long for this system (103 characters at most): " + pipeName);
        var folder = Path.GetDirectoryName(pipeName)!;
        Directory.CreateDirectory(folder);
        if (string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(RunFolder), StringComparison.Ordinal))
        {
            try { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { /* not ours to change; the socket itself is user-only */ }
        }
    }

    static string SocketPath(string kind, string? home)
    {
        if (!IsCustomHome && home is null) return Path.Combine(RunFolder, kind + ".sock");
        // a separate data folder (a test's own wallet): a short name made from its path, in the temp folder
        var tag = ShortHash((home ?? Home).TrimEnd('/').ToLowerInvariant())[..10];
        return Path.Combine(Path.GetTempPath(), "byoa-" + tag + "-" + kind + ".sock");
    }

    static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
}
