using System.Diagnostics;
using System.Text;
using Avalonia.Platform;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Desktop;

/// <summary>The file that says whether the wallet starts with the session; a test gives one that keeps its files in a temp folder.</summary>
interface ILoginItemStore
{
    string? Read(string path);
    void Write(string path, string text);
    void Remove(string path);
}

/// <summary>The session's own tools that register an application (xdg-mime, update-desktop-database); a test records the calls instead.</summary>
interface ISessionTools
{
    bool Exists(string program);
    void Run(string program, string[] arguments, TimeSpan timeout);
}

/// <summary>Where the wallet is installed for this user: <c>AgentWallet/versions/&lt;version&gt;</c> with a <c>current</c> symlink beside it.</summary>
static class LinuxInstall
{
    public const string FolderName = "AgentWallet";

    /// <summary>The folder that holds every installed version, under the data root.</summary>
    public static string VersionsRoot(string dataRoot) => Path.Combine(dataRoot, FolderName, "versions");

    /// <summary>The stable folder of the newest installed version.</summary>
    public static string Current(string dataRoot) => Path.Combine(dataRoot, FolderName, "current");

    /// <summary>True when <paramref name="appDir"/> is a folder inside <c>versions</c> and the <c>current</c> symlink exists.</summary>
    public static bool IsInstalled(string appDir, string dataRoot)
    {
        try
        {
            var versions = Path.TrimEndingDirectorySeparator(VersionsRoot(dataRoot)) + Path.DirectorySeparatorChar;
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDir)) + Path.DirectorySeparatorChar;
            return folder.Length > versions.Length && folder.StartsWith(versions, StringComparison.Ordinal) && Directory.Exists(Current(dataRoot));
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The wallet program to register: through <c>current</c> when installed, otherwise the one that is running.</summary>
    public static string ProgramPath(string appDir, string dataRoot) =>
        IsInstalled(appDir, dataRoot) ? Path.Combine(Current(dataRoot), "Byoa.Wallet") : Path.Combine(appDir, "Byoa.Wallet");

    /// <summary>The native host to register: through <c>current</c> when installed, otherwise the one next to the wallet.</summary>
    public static string HostPath(string appDir, string dataRoot) =>
        IsInstalled(appDir, dataRoot) ? Path.Combine(Current(dataRoot), "byoa-host") : Path.Combine(appDir, "byoa-host");

    /// <summary>
    /// Points <c>current</c> at <paramref name="target"/> in one rename (a new link, then <c>mv</c> over the old), so that a reader sees either
    /// the old version or the new one, never a missing link. A link left by an earlier try is not deleted; a fresh name is used instead.
    /// </summary>
    public static void PointCurrent(string dataRoot, string target)
    {
        var current = Current(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        var pending = current + ".new";
        try
        {
            File.CreateSymbolicLink(pending, target);
        }
        catch (IOException)
        {
            pending = current + ".new-" + Guid.NewGuid().ToString("N")[..8];
            File.CreateSymbolicLink(pending, target);
        }

        File.Move(pending, current, overwrite: true);
    }
}

/// <summary>
/// What an installed wallet registers for the current Linux user: the native messaging host of the browser extension, the
/// <c>bringyourownagent://</c> links, the icon, and (when the person asks) a start with the session. The wallet runs it at every start, so a
/// file that was lost, or that points at an older copy, is put right without an installer. It writes only files of its own and removes
/// nothing but its own autostart entry.
/// </summary>
sealed class LinuxRegistration
{
    public const string DesktopId = "org.bringyourownagent.wallet.desktop";
    public const string IconName = "org.bringyourownagent.wallet";
    public const string WindowClass = "Byoa.Wallet";
    public const string LinkScheme = "bringyourownagent";

    readonly ILoginItemStore _login;
    readonly ISessionTools _tools;
    readonly Func<IReadOnlyList<(int Size, byte[] Png)>> _icons;
    readonly string _home;
    readonly string _config;
    readonly string _data;

    public LinuxRegistration(ILoginItemStore login, ISessionTools tools, string home, string configRoot, string dataRoot,
        Func<IReadOnlyList<(int Size, byte[] Png)>>? icons = null)
    {
        _login = login;
        _tools = tools;
        _home = home;
        _config = configRoot;
        _data = dataRoot;
        _icons = icons ?? DesktopIcons.Read;
    }

    /// <summary>
    /// The registration of the person running the wallet; null for a wallet with a data folder of its own (a test) unless
    /// <see cref="NativeHostManifests.RootVariable"/> names a folder made for it, so that no test touches the real desktop.
    /// </summary>
    public static LinuxRegistration? ForThisUser()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var root = Environment.GetEnvironmentVariable(NativeHostManifests.RootVariable);
        if (WalletPaths.IsCustomHome && string.IsNullOrWhiteSpace(root)) return null;
        var home = string.IsNullOrWhiteSpace(root) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetFullPath(root);
        var config = string.IsNullOrWhiteSpace(root) ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Path.Combine(home, ".config");
        var data = string.IsNullOrWhiteSpace(root) ? WalletPaths.DataRoot : Path.Combine(home, ".local", "share");
        return new LinuxRegistration(new FileLoginItemStore(), new SessionTools(), home, config, data);
    }

    /// <summary>
    /// Writes the browser manifests, the extension copy, the desktop entry and the icons, and tells the desktop about the link scheme.
    /// </summary>
    /// <returns>What it did, one line per thing, for <c>Byoa.Wallet --register</c> to print.</returns>
    public IReadOnlyList<string> Apply(string appDir)
    {
        var lines = new List<string>();
        var host = LinuxInstall.HostPath(appDir, _data);
        var extension = Path.Combine(appDir, "extension");
        if (File.Exists(host) && File.Exists(Path.Combine(extension, "manifest.json")))
        {
            var copied = NativeHostManifests.SyncExtension(extension, _data);
            lines.Add($"extension: {copied} file(s) written to {Path.Combine(_data, "BYOA", "extension")}");
            var browsers = NativeHostManifests.RegisterLinux(host, File.ReadAllText(Path.Combine(extension, "manifest.json")), _home, _config);
            if (browsers.Count == 0) lines.Add("browser host: no Chromium browser was found");
            foreach (var (browser, result) in browsers) lines.Add("browser " + browser + ": " + result);
        }
        else
        {
            lines.Add("browser host: not found next to the wallet; nothing was registered for browsers");
        }

        var applications = Path.Combine(_data, "applications");
        var entry = Path.Combine(applications, DesktopId);
        var entryWritten = WriteIfDifferent(entry, DesktopEntry(LinuxInstall.ProgramPath(appDir, _data)));
        lines.Add("desktop entry: " + (entryWritten ? "written" : "up to date") + " (" + entry + ")");
        lines.Add("icons: " + InstallIcons() + " file(s) written");
        if (_tools.Exists("xdg-mime"))
        {
            _tools.Run("xdg-mime", ["default", DesktopId, "x-scheme-handler/" + LinkScheme], TimeSpan.FromSeconds(10));
            lines.Add("link scheme: xdg-mime default " + DesktopId + " x-scheme-handler/" + LinkScheme);
        }
        else
        {
            lines.Add("link scheme: xdg-mime is not installed; the desktop entry is written but not made the default");
        }

        if (_tools.Exists("update-desktop-database")) _tools.Run("update-desktop-database", [applications], TimeSpan.FromSeconds(10));
        return lines;
    }

    /// <summary>True when the autostart entry starts this copy of the wallet in the background.</summary>
    public bool StartsAtLogin(string appDir) =>
        _login.Read(LoginItemPath()) is { } text && text.Contains(Quote(LinuxInstall.ProgramPath(appDir, _data)), StringComparison.Ordinal);

    public void SetStartAtLogin(string appDir, bool on)
    {
        if (on) _login.Write(LoginItemPath(), LoginItem(LinuxInstall.ProgramPath(appDir, _data)));
        else _login.Remove(LoginItemPath());
    }

    string LoginItemPath() => Path.Combine(_config, "autostart", DesktopId);

    /// <summary>The desktop entry a click on a <c>bringyourownagent://</c> link starts.</summary>
    string DesktopEntry(string program) => string.Join('\n',
    [
        "[Desktop Entry]",
        "Type=Application",
        "Name=Agent wallet",
        "Comment=Let web pages use your own AI models, within your limits",
        "Exec=" + Quote(program) + " --url %u",
        "Icon=" + IconName,
        "Terminal=false",
        "Categories=Utility;",
        "MimeType=x-scheme-handler/" + LinkScheme + ";",
        "StartupWMClass=" + WindowClass,
        "",
    ]);

    /// <summary>The entry that starts the wallet when the person logs in.</summary>
    string LoginItem(string program) => string.Join('\n',
    [
        "[Desktop Entry]",
        "Type=Application",
        "Name=Agent wallet",
        "Comment=Start the agent wallet when you log in",
        "Exec=" + Quote(program) + " --background",
        "Icon=" + IconName,
        "Terminal=false",
        "X-GNOME-Autostart-enabled=true",
        "",
    ]);

    /// <summary>A desktop entry argument: the path in quotes, with the characters the entry format gives a meaning escaped.</summary>
    static string Quote(string program) =>
        "\"" + program.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal) + "\"";

    /// <summary>Writes the icons that are missing or different; returns how many files it wrote.</summary>
    int InstallIcons()
    {
        var written = 0;
        foreach (var (size, png) in _icons())
        {
            if (size <= 0 || png.Length == 0) continue;
            var folder = Path.Combine(_data, "icons", "hicolor", size + "x" + size, "apps");
            var target = Path.Combine(folder, IconName + ".png");
            Directory.CreateDirectory(folder);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(png)) continue;
            var temp = target + ".tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, target, overwrite: true);
            written++;
        }

        return written;
    }

    /// <summary>
    /// Writes the text with LF line ends when the file is missing or says something else; a file of the person's own is never touched.
    /// </summary>
    /// <returns>True when it wrote the file, false when it already said the same.</returns>
    internal static bool WriteIfDifferent(string path, string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return true;
    }
}

/// <summary>The autostart entry in the person's own config folder (real files, in a temp folder in the tests).</summary>
sealed class FileLoginItemStore : ILoginItemStore
{
    public string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Write(string path, string text) => _ = LinuxRegistration.WriteIfDifferent(path, text);

    public void Remove(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the person's folder: nothing else to do */ }
    }
}

/// <summary>Runs the desktop's registration tools, with a timeout and without a window; a missing tool is not an error.</summary>
sealed class SessionTools : ISessionTools
{
    public bool Exists(string program)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(folder.Trim(), program))) return true; }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }

        return false;
    }

    public void Run(string program, string[] arguments, TimeSpan timeout)
    {
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return;
            if (!process.WaitForExit((int)timeout.TotalMilliseconds)) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or NotSupportedException)
        {
            // no tool, no desktop database: the entry works without it
        }
    }
}

/// <summary>The PNG icons the wallet carries, read from its own resources; nothing is read from a folder the person controls.</summary>
static class DesktopIcons
{
    public static IReadOnlyList<(int Size, byte[] Png)> Read()
    {
        var icons = new List<(int, byte[])>();
        foreach (var uri in AssetLoader.GetAssets(new Uri("avares://Byoa.Wallet/Assets/"), null))
        {
            var name = Path.GetFileName(uri.AbsolutePath);
            if (!name.StartsWith("icon-", StringComparison.Ordinal) || !name.EndsWith(".png", StringComparison.Ordinal)) continue;
            if (!int.TryParse(name["icon-".Length..^".png".Length], out var size) || size <= 0) continue;
            using var stream = AssetLoader.Open(uri);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            icons.Add((size, memory.ToArray()));
        }

        return icons;
    }
}