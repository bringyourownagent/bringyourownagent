using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Byoa.Wallet.Desktop;
using Byoa.Wallet.Help;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Secrets;

namespace Byoa.Wallet.Tests;

public class LinuxSecretProtectorTests
{
    static readonly byte[] Plain = Encoding.UTF8.GetBytes("{\"openrouter\":\"test-secret-0123456789abcdef\"}");

    /// <summary>A file sealed the way a wallet without a Secret Service sealed it: its key lives in the file only.</summary>
    static (byte[] Sealed, MemoryKeychainItem File) SealWithTheFileAlone()
    {
        var file = new MemoryKeychainItem();
        return (new LinuxSecretProtector(null, file).Protect(Plain), file);
    }

    [Fact]
    public void WithOnlyAKeyFileTheKeyIsMadeThereOnceAndTheFileOpensAgain()
    {
        var file = new MemoryKeychainItem();
        var protector = new LinuxSecretProtector(null, file);
        var sealedBytes = protector.Protect(Plain);
        Assert.DoesNotContain("test-secret", Encoding.UTF8.GetString(sealedBytes));
        Assert.Equal(Plain, protector.Unprotect(sealedBytes));
        Assert.Equal(1, file.Writes);                                                       // the key is made once and kept
        protector.Protect(Plain);
        Assert.Equal(1, file.Writes);
        Assert.Equal(Plain, new LinuxSecretProtector(null, file).Unprotect(sealedBytes));
        Assert.NotEqual(protector.Protect(Plain), protector.Protect(Plain));                // a fresh nonce each time
    }

    [Fact]
    public void WithOnlyASecretServiceTheKeyIsMadeThereAndTheFileStaysEmpty()
    {
        var service = new MemoryKeychainItem();
        var file = new MemoryKeychainItem();
        var protector = new LinuxSecretProtector(service, file);
        var sealedBytes = protector.Protect(Plain);
        Assert.Equal(1, service.Writes);
        Assert.Equal(0, file.Writes);
        Assert.Equal(Plain, new LinuxSecretProtector(service, file).Unprotect(sealedBytes));
        Assert.Equal(1, service.Writes);
        Assert.Equal(0, file.Writes);
    }

    [Fact]
    public void AKeyFileFromBeforeTheSecretServiceIsImportedOnceAndStaysWhereItIs()
    {
        var (sealedBytes, file) = SealWithTheFileAlone();
        var fileKey = file.Get();
        var service = new MemoryKeychainItem();

        var protector = new LinuxSecretProtector(service, file);
        Assert.Equal(Plain, protector.Unprotect(sealedBytes));
        Assert.Equal(fileKey, service.Get());                                              // the file's key, imported
        Assert.Equal(1, service.Writes);
        Assert.Equal(1, file.Writes);                                                      // the file was read, never rewritten

        Assert.Equal(Plain, new LinuxSecretProtector(service, file).Unprotect(sealedBytes));
        Assert.Equal(1, service.Writes);
        Assert.Equal(1, file.Writes);
    }

    [Fact]
    public void AKeyFileIsImportedWhenTheFirstNewKeyIsStoredAfterTheServiceAppears()
    {
        var file = new MemoryKeychainItem();
        var fileKey = RandomNumberGenerator.GetBytes(32);
        file.Set(fileKey);
        var service = new MemoryKeychainItem();

        var protector = new LinuxSecretProtector(service, file);
        var sealedBytes = protector.Protect(Plain);                                        // no sealed file yet
        Assert.Equal(fileKey, service.Get());
        Assert.Equal(Plain, protector.Unprotect(sealedBytes));
        Assert.Equal(1, file.Writes);
    }

    [Fact]
    public void WithTheSameKeyInBothStoresNeitherStoreIsChanged()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var service = new MemoryKeychainItem();
        service.Set(key);
        var file = new MemoryKeychainItem();
        file.Set(key);
        var sealedBytes = new LinuxSecretProtector(null, file).Protect(Plain);

        var protector = new LinuxSecretProtector(service, file);
        Assert.Equal(Plain, protector.Unprotect(sealedBytes));
        protector.Protect(Plain);
        Assert.Equal(1, service.Writes);
        Assert.Equal(1, file.Writes);
    }

    [Fact]
    public void WithDifferentKeysTheServiceIsTriedFirstThenTheFilesKeyIsStoredInTheService()
    {
        var (sealedBytes, file) = SealWithTheFileAlone();
        var fileKey = file.Get();
        var service = new MemoryKeychainItem();
        service.Set(RandomNumberGenerator.GetBytes(32));                                    // a key that does not open the file

        var protector = new LinuxSecretProtector(service, file);
        Assert.Equal(Plain, protector.Unprotect(sealedBytes));                              // the file's key opens it
        Assert.Equal(fileKey, service.Get());                                               // and is stored in the service instead
        Assert.Equal(2, service.Writes);                                                    // the wrong key, then the right one
        Assert.Equal(1, file.Writes);

        Assert.Equal(Plain, new LinuxSecretProtector(service, file).Unprotect(sealedBytes));
        Assert.Equal(2, service.Writes);
    }

    [Fact]
    public void WithDifferentKeysAndTheRightOneInTheServiceTheFileIsLeftAlone()
    {
        var service = new MemoryKeychainItem();
        var throwawayFile = new MemoryKeychainItem();
        var sealedBytes = new LinuxSecretProtector(service, throwawayFile).Protect(Plain);   // sealed with the service's key
        var serviceKey = service.Get();
        var file = new MemoryKeychainItem();
        file.Set(RandomNumberGenerator.GetBytes(32));

        var protector = new LinuxSecretProtector(service, file);
        Assert.Equal(Plain, protector.Unprotect(sealedBytes));
        Assert.Equal(serviceKey, service.Get());
        Assert.Equal(1, service.Writes);                                                    // not replaced
        Assert.Equal(1, file.Writes);
    }

    [Fact]
    public void NeitherKeyOpensTheFileIsAnErrorAndNothingIsOverwritten()
    {
        var (sealedBytes, file) = SealWithTheFileAlone();
        file.Set(RandomNumberGenerator.GetBytes(32));
        var fileWrites = file.Writes;
        var service = new MemoryKeychainItem();
        service.Set(RandomNumberGenerator.GetBytes(32));
        var serviceWrites = service.Writes;

        var protector = new LinuxSecretProtector(service, file);
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(sealedBytes));
        Assert.Equal(fileWrites, file.Writes);
        Assert.Equal(serviceWrites, service.Writes);
    }

    [Fact]
    public void ADamagedFileOrOneForAnotherPurposeDoesNotOpen()
    {
        var file = new MemoryKeychainItem();
        var protector = new LinuxSecretProtector(null, file);
        var sealedBytes = protector.Protect(Plain);
        var flipped = sealedBytes.ToArray();
        flipped[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(flipped));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Encoding.UTF8.GetBytes("not a sealed file")));
        Assert.ThrowsAny<CryptographicException>(() => new LinuxSecretProtector(null, file, "pairings").Unprotect(sealedBytes));
    }

    [Fact]
    public void AStoreThatSaysNoIsAnErrorTheSecretStoreCanHandleNeverAnOverwrite()
    {
        var file = new MemoryKeychainItem();
        var sealedBytes = new LinuxSecretProtector(null, file).Protect(Plain);
        Assert.ThrowsAny<CryptographicException>(() => new LinuxSecretProtector(null, new MemoryKeychainItem()).Unprotect(sealedBytes));   // no key
        file.RefuseReads = true;
        Assert.ThrowsAny<CryptographicException>(() => new LinuxSecretProtector(null, file).Unprotect(sealedBytes));                       // the store refuses
        Assert.ThrowsAny<CryptographicException>(() => new LinuxSecretProtector(null, file).Protect(Plain));
        file.RefuseReads = false;

        var closed = new MemoryKeychainItem { RefuseWrites = true };
        Assert.ThrowsAny<CryptographicException>(() => new LinuxSecretProtector(null, closed).Protect(Plain));                             // nothing is sealed
    }

    [Fact]
    public void TheSecretStoreRunsOverTheLinuxProtectorAndKeepsAnUnreadableFile()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "secrets.dat");
        var item = new MemoryKeychainItem();
        var store = new SecretStore(path, new LinuxSecretProtector(null, item));
        store.Set("openrouter", "test-secret-0123456789abcdef");
        Assert.DoesNotContain("test-secret", File.ReadAllText(path, Encoding.Latin1));
        Assert.Equal("test-secret-0123456789abcdef", new SecretStore(path, new LinuxSecretProtector(null, item)).Get("openrouter"));

        var lost = new SecretStore(path, new LinuxSecretProtector(null, new MemoryKeychainItem()));
        Assert.False(lost.Has("openrouter"));
        Assert.True(lost.Unreadable);
        lost.Set("openrouter", "test-secret-new-value-0123456789");
        Assert.False(lost.Unreadable);
        Assert.Single(Directory.GetFiles(dir, "secrets.dat.unreadable-*"));
    }

    [Fact]
    public void ASecretsFileSealedWithTheKeyFileOpensWhenTheServiceAppearsAndTheServiceGetsTheKey()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "secrets.dat");
        var file = new MemoryKeychainItem();
        new SecretStore(path, new LinuxSecretProtector(null, file)).Set("openrouter", "test-secret-0123456789abcdef");

        var service = new MemoryKeychainItem();
        var after = new SecretStore(path, new LinuxSecretProtector(service, file));
        Assert.Equal("test-secret-0123456789abcdef", after.Get("openrouter"));
        Assert.False(after.Unreadable);
        Assert.Equal(file.Get(), service.Get());
    }
}

public class FileSecretKeyStoreTests
{
    [Fact]
    public void TheKeyFileIsMadePrivateAndADamagedOneIsRefusedNeverReplaced()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "secrets.key");
        var store = new FileSecretKeyStore(path);
        Assert.Null(store.Get());

        var key = RandomNumberGenerator.GetBytes(32);
        store.Set(key);
        Assert.Equal(key, store.Get());
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        File.WriteAllBytes(path, new byte[5]);
        Assert.Throws<IOException>(() => store.Get());              // damaged: an error, not a new key over it
        Assert.Equal(5, new FileInfo(path).Length);
    }

    [Fact]
    public void AKeyOfTheRightLengthIsStoredOnceAndReadBackFromAnotherInstance()
    {
        var path = Path.Combine(TestFolder.New(), "secrets.key");
        var key = RandomNumberGenerator.GetBytes(32);
        new FileSecretKeyStore(path).Set(key);
        Assert.Equal(key, new FileSecretKeyStore(path).Get());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "secrets.key.tmp"));   // the replacement was whole
    }
}

public class LinuxSecretsTests
{
    [LinuxFact]
    public void TheStorageChoiceIsTheSecretServiceOrTheKeyFileAndTheProtectorIsTheLinuxOne()
    {
        Assert.Contains(SecretProtectors.Storage, new[] { SecretStorage.SecretService, SecretStorage.KeyFile });
        Assert.IsType<LinuxSecretProtector>(LinuxSecrets.CreateProtector("secrets"));
    }
}

public class LinuxHelpTextTests
{
    [Fact]
    public void HelpSaysHowKeysAreProtectedOnThisSystemAndLeavesNoPlaceholder()
    {
        var privacy = HelpCatalog.Get("privacy");
        Assert.DoesNotContain("{{key-protection}}", privacy);
        Assert.Contains(SecretProtectors.StorageSentence, privacy, StringComparison.Ordinal);
        Assert.Contains("never logged or returned by the wallet", privacy);
    }
}

public class LinuxInstallTests
{
    [Fact]
    public void OnlyACopyInsideVersionsWithACurrentFolderCountsAsInstalled()
    {
        var data = TestFolder.New();
        var versions = Path.Combine(data, "AgentWallet", "versions", "0.30.1");
        Directory.CreateDirectory(versions);
        Assert.False(LinuxInstall.IsInstalled(versions, data));                       // no `current` yet
        Assert.Equal(Path.Combine(versions, "Byoa.Wallet"), LinuxInstall.ProgramPath(versions, data));
        Assert.Equal(Path.Combine(versions, "byoa-host"), LinuxInstall.HostPath(versions, data));

        Directory.CreateDirectory(Path.Combine(data, "AgentWallet", "current"));
        Assert.True(LinuxInstall.IsInstalled(versions, data));
        Assert.False(LinuxInstall.IsInstalled(data, data));                            // not under versions
        Assert.False(LinuxInstall.IsInstalled(Path.Combine(data, "AgentWallet", "versions"), data));   // the versions folder itself is no version
        var current = Path.Combine(data, "AgentWallet", "current");
        Assert.Equal(Path.Combine(current, "Byoa.Wallet"), LinuxInstall.ProgramPath(versions, data));
        Assert.Equal(Path.Combine(current, "byoa-host"), LinuxInstall.HostPath(versions, data));

        var plain = TestFolder.New();
        Assert.False(LinuxInstall.IsInstalled(plain, data));
        Assert.Equal(Path.Combine(plain, "Byoa.Wallet"), LinuxInstall.ProgramPath(plain, data));
    }
}

sealed class RecordingLoginStore : ILoginItemStore
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public string? Read(string path) => Files.TryGetValue(path, out var text) ? text : null;
    public void Write(string path, string text) => Files[path] = text;
    public void Remove(string path) => Files.Remove(path);
}

sealed class RecordingSessionTools : ISessionTools
{
    public HashSet<string> Present { get; } = new(StringComparer.Ordinal) { "xdg-mime", "update-desktop-database" };
    public List<(string Program, string[] Arguments)> Calls { get; } = [];

    public bool Exists(string program) => Present.Contains(program);
    public void Run(string program, string[] arguments, TimeSpan timeout) => Calls.Add((program, arguments));
}

public class LinuxRegistrationTests
{
    /// <summary>The desktop entry escapes a backslash as two; a Windows path in a unit test carries some.</summary>
    static string QuoteForEntry(string program) => program.Replace("\\", "\\\\", StringComparison.Ordinal);

    static string ExtensionManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "wallet", "extension", "manifest.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, "wallet", "extension", "manifest.json"));
    }

    sealed record Layout(string Home, string Config, string Data, string App, LinuxRegistration Registration,
        RecordingLoginStore Login, RecordingSessionTools Tools);

    static Layout Make(bool installed = false, string? version = null)
    {
        var home = TestFolder.New();
        var config = Path.Combine(home, ".config");
        var data = Path.Combine(home, ".local", "share");
        var app = installed
            ? Path.Combine(data, "AgentWallet", "versions", version ?? "0.30.1")
            : TestFolder.New();
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Byoa.Wallet"), "program");
        File.WriteAllText(Path.Combine(app, "byoa-host"), "host");
        Directory.CreateDirectory(Path.Combine(app, "extension"));
        File.WriteAllText(Path.Combine(app, "extension", "manifest.json"), ExtensionManifest());

        var login = new RecordingLoginStore();
        var tools = new RecordingSessionTools();
        (int, byte[])[] icons = [(16, [1, 2, 3]), (128, [4, 5, 6])];
        var registration = new LinuxRegistration(login, tools, home, config, data, () => icons);
        return new Layout(home, config, data, app, registration, login, tools);
    }

    [Fact]
    public void TheHostIsRegisteredForEveryBrowserInstalledAndTheExtensionAndLinkArePutInPlace()
    {
        var l = Make();
        Directory.CreateDirectory(Path.Combine(l.Config, "google-chrome"));
        Directory.CreateDirectory(Path.Combine(l.Config, "BraveSoftware", "Brave-Browser"));
        Directory.CreateDirectory(Path.Combine(l.Home, "snap", "chromium", "common", "chromium"));
        Directory.CreateDirectory(Path.Combine(l.Home, ".var", "app", "com.google.Chrome", "config", "google-chrome"));

        l.Registration.Apply(l.App);

        foreach (var folder in new[]
                 {
                     Path.Combine(l.Config, "google-chrome"),
                     Path.Combine(l.Config, "BraveSoftware", "Brave-Browser"),
                     Path.Combine(l.Home, "snap", "chromium", "common", "chromium"),
                     Path.Combine(l.Home, ".var", "app", "com.google.Chrome", "config", "google-chrome"),
                 })
        {
            var manifest = Path.Combine(folder, "NativeMessagingHosts", "org.bringyourownagent.wallet.json");
            Assert.True(File.Exists(manifest), manifest);
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            Assert.Equal("org.bringyourownagent.wallet", json.RootElement.GetProperty("name").GetString());
            Assert.Equal(Path.Combine(l.App, "byoa-host"), json.RootElement.GetProperty("path").GetString());
            Assert.Equal(["chrome-extension://niobopdoiopjilmlonjcgllpmgjemhpk/"],
                json.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(o => o.GetString()).ToArray());
        }

        Assert.False(Directory.Exists(Path.Combine(l.Config, "chromium")));                  // not installed: nothing written
        Assert.False(Directory.Exists(Path.Combine(l.Config, "microsoft-edge")));
        Assert.True(File.Exists(Path.Combine(l.Data, "BYOA", "extension", "manifest.json")));

        var entry = File.ReadAllText(Path.Combine(l.Data, "applications", "org.bringyourownagent.wallet.desktop"));
        Assert.Contains("Exec=\"" + QuoteForEntry(Path.Combine(l.App, "Byoa.Wallet")) + "\" --url %u", entry);
        Assert.Contains("Icon=org.bringyourownagent.wallet", entry);
        Assert.Contains("Categories=Utility;", entry);
        Assert.Contains("MimeType=x-scheme-handler/bringyourownagent;", entry);
        Assert.Contains("StartupWMClass=Byoa.Wallet", entry);
        Assert.Contains("[Desktop Entry]", entry);
        Assert.DoesNotContain("\r", entry);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(l.Data, "icons", "hicolor", "16x16", "apps", "org.bringyourownagent.wallet.png")));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(Path.Combine(l.Data, "icons", "hicolor", "128x128", "apps", "org.bringyourownagent.wallet.png")));

        Assert.Contains(l.Tools.Calls, call => call.Program == "xdg-mime"
            && call.Arguments.SequenceEqual(["default", "org.bringyourownagent.wallet.desktop", "x-scheme-handler/bringyourownagent"]));
        Assert.Contains(l.Tools.Calls, call => call.Program == "update-desktop-database"
            && call.Arguments.SequenceEqual([Path.Combine(l.Data, "applications")]));
    }

    [Fact]
    public void ASessionWithoutTheToolsIsLeftAloneAndASecondStartWritesNothingNewOverTheSame()
    {
        var l = Make();
        l.Tools.Present.Clear();
        Directory.CreateDirectory(Path.Combine(l.Config, "google-chrome"));
        l.Registration.Apply(l.App);
        Assert.Empty(l.Tools.Calls);

        var manifest = Path.Combine(l.Config, "google-chrome", "NativeMessagingHosts", "org.bringyourownagent.wallet.json");
        var written = File.GetLastWriteTimeUtc(manifest);
        var entry = Path.Combine(l.Data, "applications", "org.bringyourownagent.wallet.desktop");
        var entryWritten = File.GetLastWriteTimeUtc(entry);
        l.Registration.Apply(l.App);
        Assert.Equal(written, File.GetLastWriteTimeUtc(manifest));
        Assert.Equal(entryWritten, File.GetLastWriteTimeUtc(entry));
    }

    [Fact]
    public void StartAtLoginWritesAnEntryThatStartsThisCopyAndRemovesOnlyThatEntry()
    {
        var l = Make();
        // the real store writes real files into the temp folder, as it writes into ~/.config/autostart for the person
        var registration = new LinuxRegistration(new FileLoginItemStore(), l.Tools, l.Home, l.Config, l.Data, () => []);
        Assert.False(registration.StartsAtLogin(l.App));
        var other = Path.Combine(l.Config, "autostart", "someone-else.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "kept");

        registration.SetStartAtLogin(l.App, true);
        var entry = Path.Combine(l.Config, "autostart", "org.bringyourownagent.wallet.desktop");
        Assert.True(File.Exists(entry));
        var text = File.ReadAllText(entry);
        Assert.Contains("Exec=\"" + QuoteForEntry(Path.Combine(l.App, "Byoa.Wallet")) + "\" --background", text);
        Assert.Contains("X-GNOME-Autostart-enabled=true", text);
        Assert.True(registration.StartsAtLogin(l.App));

        registration.SetStartAtLogin(l.App, false);
        Assert.False(File.Exists(entry));
        Assert.False(registration.StartsAtLogin(l.App));
        Assert.Equal("kept", File.ReadAllText(other));                                       // not the wallet's: left alone
    }

    [Fact]
    public void AnInstalledCopyRegistersTheStableCurrentFolder()
    {
        var l = Make(installed: true);
        var current = Path.Combine(l.Data, "AgentWallet", "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "byoa-host"), "host");                  // what install.sh puts under current
        Directory.CreateDirectory(Path.Combine(l.Config, "google-chrome"));
        l.Registration.Apply(l.App);

        var manifest = Path.Combine(l.Config, "google-chrome", "NativeMessagingHosts", "org.bringyourownagent.wallet.json");
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        Assert.Equal(Path.Combine(l.Data, "AgentWallet", "current", "byoa-host"), json.RootElement.GetProperty("path").GetString());
        var entry = File.ReadAllText(Path.Combine(l.Data, "applications", "org.bringyourownagent.wallet.desktop"));
        Assert.Contains("Exec=\"" + QuoteForEntry(Path.Combine(l.Data, "AgentWallet", "current", "Byoa.Wallet")) + "\" --url %u", entry);

        Assert.False(l.Registration.StartsAtLogin(l.App));
        l.Registration.SetStartAtLogin(l.App, true);
        Assert.Contains(QuoteForEntry(Path.Combine(l.Data, "AgentWallet", "current", "Byoa.Wallet")), l.Login.Files[Path.Combine(l.Config, "autostart", "org.bringyourownagent.wallet.desktop")]);
    }

    [Fact]
    public void TheIconsBesideTheProgramAreReadWithoutAvalonia()
    {
        var dir = TestFolder.New();
        var icons = Path.Combine(dir, "icons");
        Directory.CreateDirectory(icons);
        File.WriteAllBytes(Path.Combine(icons, "icon-16.png"), [1, 2]);
        File.WriteAllBytes(Path.Combine(icons, "icon-256.png"), [3, 4]);
        File.WriteAllText(Path.Combine(icons, "tray.png"), "not an icon");

        var read = DesktopIcons.ReadFromFiles(dir).OrderBy(icon => icon.Size).ToArray();
        Assert.Equal([16, 256], read.Select(icon => icon.Size).ToArray());
        Assert.Equal([1, 2], read[0].Png);
        Assert.Equal([3, 4], read[1].Png);
        Assert.Empty(DesktopIcons.ReadFromFiles(TestFolder.New()));
    }

    [LinuxFact]
    public void ATestWalletWithoutAFolderForItRegistersNothing()
    {
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        var oldRoot = Environment.GetEnvironmentVariable(NativeHostManifests.RootVariable);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, TestFolder.New());
        Environment.SetEnvironmentVariable(NativeHostManifests.RootVariable, null);
        try
        {
            Assert.Null(LinuxRegistration.ForThisUser());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
            Environment.SetEnvironmentVariable(NativeHostManifests.RootVariable, oldRoot);
        }
    }
}

public class LinuxBridgeFolderTests
{
    [LinuxFact]
    public void AnInstalledWalletThroughTheCurrentSymlinkRecognisesItsBridge()
    {
        var data = TestFolder.New();
        var version = Path.Combine(data, "AgentWallet", "versions", "0.30.1");
        Directory.CreateDirectory(version);
        var bridge = Path.Combine(version, "byoa-mcp");
        File.WriteAllText(bridge, "bridge");
        var current = Path.Combine(data, "AgentWallet", "current");
        Directory.CreateSymbolicLink(current, version);                       // what install.sh makes

        Assert.True(AgentPipeServer.IsBridgeIn(bridge, current + Path.DirectorySeparatorChar));
        Assert.True(AgentPipeServer.IsBridgeIn(bridge, version));
        Assert.False(AgentPipeServer.IsBridgeIn(Path.Combine(current, "other"), current));
    }
}

public class LinuxAgentProcessInfoTests
{
    [LinuxFact]
    public void TheWalletReadsProcessFactsFromProcOnLinux()
    {
        Assert.IsType<LinuxAgentProcessInfo>(AgentProcessInfo.ForThisSystem());

        var current = Process.GetCurrentProcess();
        Assert.Equal(current.MainModule!.FileName, LinuxAgentProcessInfo.ImagePath(Environment.ProcessId));
        var seen = LinuxAgentProcessInfo.Describe(Environment.ProcessId);
        Assert.NotNull(seen);
        Assert.Equal((uint)Environment.ProcessId, seen.ClientProcessId);
        Assert.False(string.IsNullOrEmpty(seen.ParentImagePath));
        Assert.True(seen.ClientStartTime is { } started && Math.Abs((started - current.StartTime).TotalSeconds) < 5);
        Assert.Null(LinuxAgentProcessInfo.ImagePath(int.MaxValue));
    }

    [LinuxFact]
    public async Task ASocketReportsTheSameProcessAtTheOtherEnd()
    {
        var path = Path.Combine(Path.GetTempPath(), "byoa-l-" + Guid.NewGuid().ToString("N")[..10] + ".sock");
        using var server = new NamedPipeServerStream(path, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", path, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(3000);
        await accepted;

        var seen = new LinuxAgentProcessInfo().Read(server.SafePipeHandle);
        Assert.NotNull(seen);
        Assert.Equal((uint)Environment.ProcessId, seen.ClientProcessId);
        Assert.Equal(Process.GetCurrentProcess().MainModule!.FileName, seen.ClientImagePath);
        Assert.False(string.IsNullOrEmpty(seen.ParentImagePath));
    }
}