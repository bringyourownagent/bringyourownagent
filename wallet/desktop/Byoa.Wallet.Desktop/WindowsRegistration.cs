using System.Runtime.Versioning;
using Byoa.Wallet.Protocol;
using Win32Registry = Microsoft.Win32.Registry;

namespace Byoa.Wallet.Desktop;

/// <summary>The few registry keys of the current user that the registration writes; a test stands in for them with a dictionary.</summary>
interface IRegistryStore
{
    void SetValue(string key, string? name, string value);
    string? GetValue(string key, string? name);

    /// <summary>Removes the key and everything under it; nothing happens when it is not there.</summary>
    void DeleteTree(string key);

    /// <summary>Removes one value of a key; nothing happens when it is not there.</summary>
    void DeleteValue(string key, string name);
}

/// <summary><c>HKEY_CURRENT_USER</c> itself.</summary>
[SupportedOSPlatform("windows")]
sealed class UserRegistry : IRegistryStore
{
    public void SetValue(string key, string? name, string value)
    {
        using var subKey = Win32Registry.CurrentUser.CreateSubKey(key);
        subKey.SetValue(name ?? "", value);
    }

    public string? GetValue(string key, string? name)
    {
        using var subKey = Win32Registry.CurrentUser.OpenSubKey(key);
        return subKey?.GetValue(name ?? "") as string;
    }

    public void DeleteTree(string key) => Win32Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

    public void DeleteValue(string key, string name)
    {
        using var subKey = Win32Registry.CurrentUser.OpenSubKey(key, writable: true);
        subKey?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// What the installed wallet registers for the current Windows user, the same as install.ps1 did for the zip: the native messaging host of
/// the browser extension, the <c>bringyourownagent://</c> links, and (when the person asks) a start with Windows. The Setup.exe of the
/// installer runs it after an install and an update (see <see cref="Program"/>), and the wallet runs it again at every start, so a key that
/// was lost or points elsewhere is put right. It writes keys and one small file next to the program, and removes nothing but its own keys.
/// </summary>
sealed class WindowsRegistration(IRegistryStore registry)
{
    const string HostName = NativeHostManifests.HostName;
    const string ProtocolKey = @"Software\Classes\bringyourownagent";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Agent wallet";

    // Chrome and Chromium (the second one serves Chromium builds such as Playwright's)
    static readonly string[] HostKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Chromium\NativeMessagingHosts\" + HostName,
    ];

    /// <summary>Registers the program in <paramref name="appDir"/>: the host manifest file, the host keys and the link scheme.</summary>
    public void Apply(string appDir)
    {
        var walletExe = Path.Combine(appDir, "Byoa.Wallet.exe");
        var hostExe = Path.Combine(appDir, "byoa-host.exe");
        var extensionManifest = Path.Combine(appDir, "extension", "manifest.json");
        if (File.Exists(hostExe) && File.Exists(extensionManifest))
        {
            var manifestPath = Path.Combine(appDir, HostName + ".json");
            var text = NativeHostManifests.ManifestJson(hostExe, NativeHostManifests.ExtensionId(File.ReadAllText(extensionManifest)));
            if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != text) File.WriteAllText(manifestPath, text, new System.Text.UTF8Encoding(false));
            foreach (var key in HostKeys)
            {
                if (registry.GetValue(key, null) != manifestPath) registry.SetValue(key, null, manifestPath);
            }
        }

        // a link starts the wallet, or hands the link to the wallet that is already running
        var command = $"\"{walletExe}\" --url \"%1\"";
        if (registry.GetValue(ProtocolKey + @"\shell\open\command", null) == command) return;
        registry.SetValue(ProtocolKey, null, "URL:Bring Your Own Agent link");
        registry.SetValue(ProtocolKey, "URL Protocol", "");
        registry.SetValue(ProtocolKey + @"\DefaultIcon", null, $"\"{walletExe}\",0");
        registry.SetValue(ProtocolKey + @"\shell\open\command", null, command);
    }

    /// <summary>
    /// Takes the registration back at an uninstall: only the keys that still point into <paramref name="appDir"/> (a wallet installed some
    /// other way keeps its own), and the start with Windows. Files are left alone; so are the person's settings.
    /// </summary>
    public void Remove(string appDir)
    {
        var folder = Path.TrimEndingDirectorySeparator(appDir) + Path.DirectorySeparatorChar;
        foreach (var key in HostKeys)
        {
            if (registry.GetValue(key, null) is { } path && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) registry.DeleteTree(key);
        }

        if (registry.GetValue(ProtocolKey + @"\shell\open\command", null) is { } command && command.Contains(folder, StringComparison.OrdinalIgnoreCase))
            registry.DeleteTree(ProtocolKey);
        if (registry.GetValue(RunKey, RunName) is { } run && run.Contains(folder, StringComparison.OrdinalIgnoreCase)) SetStartWithWindows(appDir, false);
    }

    /// <summary>True when the wallet starts with Windows (in the tray, without a window).</summary>
    public bool StartsWithWindows(string appDir) =>
        registry.GetValue(RunKey, RunName) is { } run && run.Contains(Path.TrimEndingDirectorySeparator(appDir), StringComparison.OrdinalIgnoreCase);

    public void SetStartWithWindows(string appDir, bool on)
    {
        if (on) registry.SetValue(RunKey, RunName, $"\"{Path.Combine(appDir, "Byoa.Wallet.exe")}\" --background");
        else if (registry.GetValue(RunKey, RunName) is not null) registry.DeleteValue(RunKey, RunName);
    }
}
