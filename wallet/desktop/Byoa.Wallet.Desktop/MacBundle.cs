using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Desktop;

/// <summary>Where the wallet is on macOS: inside its application bundle (<c>Agent Wallet.app/Contents/MacOS</c>), or run from a plain folder.</summary>
static class MacBundle
{
    public const string FolderName = "Agent Wallet.app";

    /// <summary>The <c>.app</c> folder the running wallet is inside; null when it runs from a plain folder.</summary>
    public static string? Current => BundleOf(AppContext.BaseDirectory);

    internal static string? BundleOf(string path)
    {
        for (var folder = Path.TrimEndingDirectorySeparator(path); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            if (folder.EndsWith(".app", StringComparison.Ordinal)) return folder;
        return null;
    }

    /// <summary>The folder of the browser extension that belongs to this copy (in the bundle's Resources, or beside the program).</summary>
    public static string? ExtensionFolder()
    {
        var candidates = new List<string>();
        if (Current is { } bundle) candidates.Add(Path.Combine(bundle, "Contents", "Resources", "extension"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "extension"));
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "manifest.json")));
    }

    /// <summary>
    /// Tells every installed Chromium browser where the native host is, so that the extension finds the wallet. Runs at each start, in the
    /// background; a failure is logged and never stops the wallet. A wallet run on a data folder of its own (a test) touches no browser unless
    /// <see cref="NativeHostManifests.RootVariable"/> points at a folder made for it.
    /// </summary>
    public static void RegisterBrowsers()
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (WalletPaths.IsCustomHome && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NativeHostManifests.RootVariable))) return;
        try
        {
            var host = Path.Combine(AppContext.BaseDirectory, "byoa-host");
            if (!File.Exists(host) || ExtensionFolder() is not { } extension) return;
            var support = NativeHostManifests.ApplicationSupport();
            NativeHostManifests.SyncExtension(extension, support);
            NativeHostManifests.Register(host, File.ReadAllText(Path.Combine(extension, "manifest.json")), support);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            ErrorLog.Write(WalletPaths.Home, e);
        }
    }
}
