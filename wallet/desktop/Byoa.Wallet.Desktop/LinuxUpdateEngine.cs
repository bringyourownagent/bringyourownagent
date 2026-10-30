using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The update of an installed Linux copy: the site's <c>latest.json</c> names the newest package, its SHA-256 and its size; the package is
/// downloaded beside the installed versions, checked, unpacked into <c>versions/&lt;version&gt;.tmp</c> and moved to
/// <c>versions/&lt;version&gt;</c>; <c>current</c> is swapped to it in one rename, and a detached helper starts the new wallet once this one
/// has quit. Nothing is deleted: an old version stays, and a download that fails leaves the running copy untouched.
/// </summary>
sealed class LinuxUpdateEngine : IUpdateEngine
{
    /// <summary>Where the releases are: <c>latest.json</c> and the package it names.</summary>
    public const string FeedUrl = "https://bringyourownagent.org/downloads/linux";

    /// <summary>A test run (a wallet with its own data folder) may name another feed, an address on this computer or a test server.</summary>
    public const string FeedVariable = "BYOA_UPDATE_FEED";

    readonly LinuxUpdateOptions _options;
    Release? _found;

    public LinuxUpdateEngine() : this(LinuxUpdateOptions.ForThisUser()) { }

    internal LinuxUpdateEngine(LinuxUpdateOptions options) => _options = options;

    /// <summary>True only for a copy inside <c>AgentWallet/versions/&lt;v&gt;</c> with <c>AgentWallet/current</c> beside it.</summary>
    public bool CanUpdateItself => LinuxInstall.IsInstalled(_options.RunningFolder, _options.DataRoot);

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        var release = await FetchAsync(ct).ConfigureAwait(false);
        _found = null;
        if (!SemanticVersion.TryParse(_options.RunningVersion, out var running))
            throw new InvalidDataException("The wallet's own version is not one the updater understands: " + _options.RunningVersion);
        if (!SemanticVersion.TryParse(release.Version, out var offered))
            throw new InvalidDataException("The update feed names a version the updater does not understand: " + release.Version);
        if (offered.CompareTo(running) <= 0) return null;       // the same version, or an older one on the site: nothing to offer
        _found = release;
        return release.Version;
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken ct)
    {
        var release = _found ?? throw new InvalidOperationException("Check for updates first.");
        var downloads = Path.Combine(_options.InstallRoot, "downloads");
        Directory.CreateDirectory(downloads);
        var part = Path.Combine(downloads, release.File + ".part");

        var url = _options.FeedBase + "/" + release.File;
        using var response = await _options.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The update package could not be downloaded (" + (int)response.StatusCode + ").", null, response.StatusCode);

        // The header only drives the progress bar; the size that counts is the one in the feed, checked below.
        var total = response.Content.Headers.ContentLength is { } length && length > 0 ? length : release.Size;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long done = 0;
            var last = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                var percent = (int)Math.Min(100, done * 100 / Math.Max(1, total));
                if (percent == last) continue;
                last = percent;
                progress(percent);
            }
        }

        var received = new FileInfo(part).Length;
        if (received != release.Size)
            throw new InvalidDataException($"The downloaded package is not complete ({received} bytes instead of {release.Size}).");

        string hash;
        await using (var file = File.OpenRead(part))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded package is damaged: its SHA-256 does not match the one in the update feed.");

        var temp = Path.Combine(_options.VersionsRoot, release.Version + ".tmp");
        Directory.CreateDirectory(_options.VersionsRoot);
        LinuxUpdatePackage.Extract(part, temp);
        LinuxUpdatePackage.CheckPrograms(temp);
        progress(100);
    }

    /// <summary>Moves the unpacked version in place, points <c>current</c> at it, and starts the helper that brings the wallet back.</summary>
    public void ApplyAndRestart()
    {
        var release = _found ?? throw new InvalidOperationException("Check for updates first.");
        var target = Path.Combine(_options.VersionsRoot, release.Version);
        var temp = target + ".tmp";
        if (!Directory.Exists(temp)) throw new InvalidOperationException("Download the update first.");
        if (Directory.Exists(target)) throw new InvalidOperationException("Version " + release.Version + " is already installed.");
        Directory.Move(temp, target);
        LinuxInstall.PointCurrent(_options.DataRoot, target);
        _options.StartRestarted(_options.InstallRoot, _options.ProcessId);
    }

    async Task<Release> FetchAsync(CancellationToken ct)
    {
        using var response = await _options.Http.GetAsync(_options.FeedBase + "/latest.json", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The update feed is not an object.");

        string? Text(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

        var version = Text("version");
        var file = Text("file");
        var sha256 = Text("sha256");
        var size = root.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed) ? parsed : 0;
        if (version is null) throw new InvalidDataException("The update feed has no version.");
        if (!SemanticVersion.TryParse(version, out _)) throw new InvalidDataException("The update feed names a version the updater does not understand: " + version);
        if (file is null || !IsPlainFileName(file)) throw new InvalidDataException("The update feed names a file the updater will not download: " + (file ?? "none"));
        if (sha256 is null || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit)) throw new InvalidDataException("The update feed has no proper SHA-256.");
        if (size <= 0) throw new InvalidDataException("The update feed has no proper size.");
        return new Release(version, file, sha256.ToLowerInvariant(), size);
    }

    /// <summary>A name the updater may put in its downloads folder: no folder, no way out, nothing the shell would read twice.</summary>
    static bool IsPlainFileName(string name) =>
        name.Length is > 0 and <= 200 && name is not "." and not ".." && name.IndexOfAny(['/', '\\', '\0']) < 0
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    sealed record Release(string Version, string File, string Sha256, long Size);
}

/// <summary>The places the updater works in and the two things a test replaces (the site and the restart).</summary>
sealed record LinuxUpdateOptions
{
    public required HttpClient Http { get; init; }
    public required string FeedBase { get; init; }
    public required string DataRoot { get; init; }
    public required string RunningFolder { get; init; }
    public required string RunningVersion { get; init; }
    public required int ProcessId { get; init; }
    public Action<string, int> StartRestarted { get; init; } = LinuxRestart.StartDetached;

    public string InstallRoot => Path.Combine(DataRoot, LinuxInstall.FolderName);
    public string VersionsRoot => LinuxInstall.VersionsRoot(DataRoot);

    /// <summary>The running copy's settings; only a wallet with a data folder of its own may point the feed elsewhere.</summary>
    public static LinuxUpdateOptions ForThisUser()
    {
        var feed = LinuxUpdateEngine.FeedUrl;
        if (WalletPaths.IsCustomHome && Environment.GetEnvironmentVariable(LinuxUpdateEngine.FeedVariable) is { Length: > 0 } given)
            feed = given;
        return new LinuxUpdateOptions
        {
            Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
            FeedBase = feed.TrimEnd('/'),
            DataRoot = WalletPaths.DataRoot,
            RunningFolder = AppContext.BaseDirectory,
            RunningVersion = WalletPaths.Version,
            ProcessId = Environment.ProcessId,
        };
    }
}

/// <summary>
/// Unpacks a package archive into a version folder. Only <c>&lt;top&gt;/app/...</c> and <c>&lt;top&gt;/extension/...</c> are installed; the
/// installer, the licenses and the readme in the same archive are for the person, not for <c>versions</c>. An entry with <c>..</c>, an
/// absolute path, a link or a special file stops the whole unpack: the wallet installs plain files it has checked, nothing that could point
/// outside the folder.
/// </summary>
static class LinuxUpdatePackage
{
    const UnixFileMode All = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>The three programs an installed version must have; their execute bit is ensured even if an archive lost it.</summary>
    public static readonly string[] Programs = ["Byoa.Wallet", "byoa-host", "byoa-mcp"];

    public static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        if (ReadEntries(reader, destination) is null) throw new InvalidDataException("The update package is empty.");
    }

    /// <summary>Copies the app and extension entries into <paramref name="destination"/>; returns the top folder the archive used.</summary>
    static string? ReadEntries(TarReader reader, string destination)
    {
        string? top = null;
        try
        {
            while (reader.GetNextEntry() is { } entry)
            {
                // The reader turns these into the entry that follows them; one that is still here says nothing about a file.
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes
                    or TarEntryType.LongPath or TarEntryType.LongLink) continue;

                var name = entry.Name.Replace('\\', '/').TrimEnd('/');
                if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
                if (name.Length == 0 || name == ".") continue;
                var segments = name.Split('/');
                if (segments.Any(segment => segment.Length == 0 || segment is "." or "..") || Path.IsPathRooted(name) || name.Contains(':'))
                    throw new InvalidDataException("The update package has an entry that is not a plain path inside its folder: " + entry.Name);

                top ??= segments[0];
                if (segments[0] != top)
                    throw new InvalidDataException("The update package has more than one top folder: " + entry.Name);
                if (segments.Length == 1) continue;                 // the top folder itself

                var rest = string.Join('/', segments[1..]);
                string? target = rest switch
                {
                    "app" => destination,
                    _ when rest.StartsWith("app/", StringComparison.Ordinal) => Path.Combine(destination, rest[4..]),
                    "extension" => Path.Combine(destination, "extension"),
                    _ when rest.StartsWith("extension/", StringComparison.Ordinal) => Path.Combine(destination, "extension", rest[10..]),
                    _ => null,                                      // install.sh, uninstall.sh, licenses, README: not part of a version
                };
                if (target is null) continue;
                Inside(destination, target);

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                    case TarEntryType.DirectoryList:
                        Directory.CreateDirectory(target);
                        SetMode(target, entry.Mode, directory: true);
                        break;
                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                    case TarEntryType.ContiguousFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                            entry.DataStream?.CopyTo(output);
                        SetMode(target, entry.Mode, directory: false);
                        break;
                    case TarEntryType.SymbolicLink:
                    case TarEntryType.HardLink:
                        throw new InvalidDataException("The update package has a link, which the wallet does not install: " + entry.Name);
                    default:
                        throw new InvalidDataException("The update package has an entry the wallet does not install: " + entry.Name);
                }
            }
        }
        catch (EndOfStreamException e)
        {
            // A package cut off before its end: treated like any other damaged package, not like a disk error.
            throw new InvalidDataException("The update package is not a complete tar archive.", e);
        }

        return top;
    }

    /// <summary>Stops when one of the programs is missing and makes sure the three can be started.</summary>
    public static void CheckPrograms(string folder)
    {
        foreach (var program in Programs)
        {
            var path = Path.Combine(folder, program);
            if (!File.Exists(path)) throw new InvalidDataException("The update package has no " + program + " program.");
            if (OperatingSystem.IsWindows()) continue;
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        }
    }

    /// <summary>True when <paramref name="target"/> is <paramref name="root"/> or inside it.</summary>
    static void Inside(string root, string target)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullTarget = Path.GetFullPath(target);
        if (fullTarget == fullRoot || fullTarget.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        throw new InvalidDataException("The update package has an entry outside its folder: " + target);
    }

    static void SetMode(string path, UnixFileMode mode, bool directory)
    {
        if (OperatingSystem.IsWindows()) return;
        mode &= All;
        if (mode == 0) mode = directory ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            File.SetUnixFileMode(path, mode);                   // also works for a directory: the same chmod underneath
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the file is still readable by its owner through the folder; the mode is not worth failing an update for
        }
    }
}

/// <summary>Starts the new wallet once this one has quit: a detached shell waits for the old process to end, then runs <c>current</c>.</summary>
static class LinuxRestart
{
    public static void StartDetached(string installRoot, int processId)
    {
        if (!OperatingSystem.IsLinux()) return;
        var wallet = Path.Combine(installRoot, "current", "Byoa.Wallet");
        var script = "while kill -0 " + processId + " 2>/dev/null; do sleep 0.2; done; exec " + ShellQuote(wallet);
        var setsid = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : File.Exists("/bin/setsid") ? "/bin/setsid" : null;
        var info = setsid is null
            ? new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo(setsid) { UseShellExecute = false, CreateNoWindow = true };
        if (setsid is not null) info.ArgumentList.Add("/bin/sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        try
        {
            using var helper = Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // The new version is in place and current points at it; the person starts it from the launcher.
        }
    }

    /// <summary>A path as one word of a POSIX shell, whatever characters it holds.</summary>
    internal static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

/// <summary>
/// A version like <c>0.30.1</c> as the update feed and the wallet write them, with an optional pre-release (<c>-beta.1</c>, which sorts
/// before the release); build metadata after <c>+</c> is ignored. Comparison follows the semantic version rules the updater needs: numbers
/// by value, a pre-release before its release, and identifiers of a pre-release left to right.
/// </summary>
sealed class SemanticVersion : IComparable<SemanticVersion>
{
    SemanticVersion(int major, int minor, int patch, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) text = text[..plus];
        string? preRelease = null;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..];
            text = text[..dash];
            if (preRelease.Length == 0 || preRelease.Split('.').Any(part => part.Length == 0 || !part.All(char.IsAsciiLetterOrDigit)))
                return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit))) return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || !int.TryParse(parts[2], out var patch)) return false;
        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;      // 1.0.0 after 1.0.0-beta
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = a[i].All(char.IsAsciiDigit);
            var bNumeric = b[i].All(char.IsAsciiDigit);
            int result;
            if (aNumeric && bNumeric)
            {
                // A long number is compared by length first, so that it never overflows.
                result = a[i].TrimStart('0').Length.CompareTo(b[i].TrimStart('0').Length);
                if (result == 0) result = string.CompareOrdinal(a[i].TrimStart('0'), b[i].TrimStart('0'));
            }
            else if (aNumeric) result = -1;                                    // numeric identifiers sort before alphanumeric ones
            else if (bNumeric) result = 1;
            else result = string.CompareOrdinal(a[i], b[i]);
            if (result != 0) return Math.Sign(result);
        }

        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => Major + "." + Minor + "." + Patch + (PreRelease is null ? "" : "-" + PreRelease);
}