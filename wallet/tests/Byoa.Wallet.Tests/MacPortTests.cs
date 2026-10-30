using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Byoa.Wallet.Desktop;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>A Keychain item kept in memory: the sealing code runs for real, and no test ever touches the Keychain of the person who runs it.</summary>
sealed class MemoryKeychainItem : IKeychainItem
{
    public static readonly MemoryKeychainItem Shared = new();

    byte[]? _value;

    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public bool RefuseReads { get; set; }
    public bool RefuseWrites { get; set; }

    public byte[]? Get()
    {
        Reads++;
        if (RefuseReads) throw new KeychainException(KeychainStatus.InteractionNotAllowed, "locked");
        return _value?.ToArray();
    }

    public void Set(byte[] value)
    {
        Writes++;
        if (RefuseWrites) throw new KeychainException(KeychainStatus.InteractionNotAllowed, "locked");
        _value = value.ToArray();
    }
}

/// <summary>A program every system has, for the tests that add or run an agent's program.</summary>
static class TestPrograms
{
    public static readonly string Runnable = OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe"
        : "/bin/sh";
}

static class TestSetup
{
    /// <summary>
    /// Away from Windows the wallet's default protector is the Keychain's; the tests seal under a key kept in memory instead (the same AES code),
    /// so that a run on a Mac never writes to the login keychain of the person who runs it.
    /// </summary>
    [ModuleInitializer]
    internal static void ProtectInMemoryAwayFromWindows()
    {
        if (!OperatingSystem.IsWindows()) SecretProtectors.TestFactory = purpose => new KeychainSecretProtector(MemoryKeychainItem.Shared, purpose);
    }
}

public class KeychainSecretProtectorTests
{
    static readonly byte[] Plain = Encoding.UTF8.GetBytes("{\"openrouter\":\"test-secret-0123456789abcdef\"}");

    [Fact]
    public void ASealedFileOpensAgainHoldsNoPlainTextAndTheKeyIsMadeOnceAndKept()
    {
        var item = new MemoryKeychainItem();
        var protector = new KeychainSecretProtector(item);
        var sealedBytes = protector.Protect(Plain);
        Assert.DoesNotContain("test-secret", Encoding.UTF8.GetString(sealedBytes));
        Assert.Equal(Plain, protector.Unprotect(sealedBytes));
        Assert.Equal(1, item.Writes);                                   // the key was made at the first sealing...
        protector.Protect(Plain);
        Assert.Equal(1, item.Writes);                                   // ...and is the same afterwards
        Assert.Equal(Plain, new KeychainSecretProtector(item).Unprotect(sealedBytes));   // a later run finds the key
        Assert.NotEqual(protector.Protect(Plain), protector.Protect(Plain));              // a fresh nonce each time
    }

    [Fact]
    public void ADamagedOrForeignFileOrAFileForAnotherPurposeDoesNotOpen()
    {
        var item = new MemoryKeychainItem();
        var protector = new KeychainSecretProtector(item);
        var sealedBytes = protector.Protect(Plain);

        var flipped = sealedBytes.ToArray();
        flipped[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(flipped));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Encoding.UTF8.GetBytes("this is not a sealed file at all, it is only text")));
        Assert.ThrowsAny<CryptographicException>(() => new KeychainSecretProtector(item, "pairings").Unprotect(sealedBytes));
    }

    [Fact]
    public void AKeyThatIsGoneOrAKeychainThatSaysNoIsAnErrorTheStoreCanHandleNeverAnOverwrite()
    {
        var item = new MemoryKeychainItem();
        var sealedBytes = new KeychainSecretProtector(item).Protect(Plain);

        Assert.ThrowsAny<CryptographicException>(() => new KeychainSecretProtector(new MemoryKeychainItem()).Unprotect(sealedBytes));   // no key in the Keychain
        item.RefuseReads = true;
        Assert.ThrowsAny<CryptographicException>(() => new KeychainSecretProtector(item).Unprotect(sealedBytes));                        // locked
        Assert.ThrowsAny<CryptographicException>(() => new KeychainSecretProtector(item).Protect(Plain));
        item.RefuseReads = false;

        var closed = new MemoryKeychainItem { RefuseWrites = true };
        Assert.ThrowsAny<CryptographicException>(() => new KeychainSecretProtector(closed).Protect(Plain));                              // the key could not be kept: nothing is sealed under it
    }

    [Fact]
    public void TheSecretStoreAndThePairingStoreWorkOverTheKeychainProtector()
    {
        var dir = TestFolder.New();
        var item = new MemoryKeychainItem();
        var file = Path.Combine(dir, "secrets.dat");
        var store = new SecretStore(file, new KeychainSecretProtector(item));
        store.Set("openrouter", " test-secret-0123456789abcdef ");
        Assert.Equal("test-secret-0123456789abcdef", new SecretStore(file, new KeychainSecretProtector(item)).Get("openrouter"));
        Assert.DoesNotContain("test-secret", File.ReadAllText(file, Encoding.Latin1));

        // the key vanished from the Keychain: no keys, not a crash; the file is kept when a new key is entered
        var lost = new SecretStore(file, new KeychainSecretProtector(new MemoryKeychainItem()));
        Assert.False(lost.Has("openrouter"));
        Assert.True(lost.Unreadable);
        lost.Set("openrouter", "test-secret-new-value-0123456789");
        Assert.False(lost.Unreadable);
        Assert.Single(Directory.GetFiles(dir, "secrets.dat.unreadable-*"));

        var pairings = new PairingStore(Path.Combine(dir, "pairings.dat"), new KeychainSecretProtector(item, "pairings"));
        pairings.Set(new Pairing("https://site.example", "https://site.example/byoa", "{}", "tok-1", DateTimeOffset.UnixEpoch));
        Assert.Equal("tok-1", new PairingStore(Path.Combine(dir, "pairings.dat"), new KeychainSecretProtector(item, "pairings")).Get("https://site.example")?.Token);
        Assert.Empty(new PairingStore(Path.Combine(dir, "pairings.dat"), new KeychainSecretProtector(item, "secrets")).All());   // another purpose reads nothing
    }
}

public class NativeHostManifestTests
{
    static string ExtensionManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "wallet", "extension", "manifest.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, "wallet", "extension", "manifest.json"));
    }

    [Fact]
    public void TheExtensionIdIsTheOneChromeGivesTheKeyInTheManifest()
    {
        // the ID install.ps1 prints for the same manifest
        Assert.Equal("niobopdoiopjilmlonjcgllpmgjemhpk", NativeHostManifests.ExtensionId(ExtensionManifest()));
        Assert.Throws<InvalidDataException>(() => NativeHostManifests.ExtensionId("{\"name\":\"no key\"}"));
    }

    [Fact]
    public void TheManifestNamesTheHostAndAllowsOnlyTheWalletsExtension()
    {
        using var json = JsonDocument.Parse(NativeHostManifests.ManifestJson("/Applications/Agent Wallet.app/Contents/MacOS/byoa-host", "abcdefghijklmnopabcdefghijklmnop"));
        var root = json.RootElement;
        Assert.Equal("org.bringyourownagent.wallet", root.GetProperty("name").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.Equal("/Applications/Agent Wallet.app/Contents/MacOS/byoa-host", root.GetProperty("path").GetString());
        Assert.Equal(["chrome-extension://abcdefghijklmnopabcdefghijklmnop/"], root.GetProperty("allowed_origins").EnumerateArray().Select(o => o.GetString()).ToArray());
    }

    [Fact]
    public void ItIsWrittenOnlyForBrowsersThatAreInstalledAndLeftAloneWhenItAlreadySaysTheSame()
    {
        var support = TestFolder.New();
        Directory.CreateDirectory(Path.Combine(support, "Google", "Chrome"));            // Chrome is installed
        Directory.CreateDirectory(Path.Combine(support, "BraveSoftware", "Brave-Browser"));
        const string host = "/tmp/Agent Wallet.app/Contents/MacOS/byoa-host";

        var first = NativeHostManifests.Register(host, ExtensionManifest(), support);
        Assert.Equal([("Google Chrome", "written"), ("Brave", "written")], first);
        var chrome = Path.Combine(support, "Google", "Chrome", "NativeMessagingHosts", "org.bringyourownagent.wallet.json");
        Assert.True(File.Exists(chrome));
        Assert.False(Directory.Exists(Path.Combine(support, "Microsoft Edge")));        // a browser that is not installed gets nothing
        Assert.False(Directory.Exists(Path.Combine(support, "Chromium")));
        var written = File.GetLastWriteTimeUtc(chrome);

        Assert.Equal([("Google Chrome", "up to date"), ("Brave", "up to date")], NativeHostManifests.Register(host, ExtensionManifest(), support));
        Assert.Equal(written, File.GetLastWriteTimeUtc(chrome));

        // the copy moved: the manifest follows it
        Assert.Equal("written", NativeHostManifests.Register("/Applications/Agent Wallet.app/Contents/MacOS/byoa-host", ExtensionManifest(), support)[0].Result);
        Assert.Contains("/Applications/Agent Wallet.app/", File.ReadAllText(chrome));
    }
}

public class ExtensionCopyTests
{
    [Fact]
    public void TheExtensionCopyIsWrittenOnceChangedFilesFollowAndNothingIsDeleted()
    {
        var source = TestFolder.New();
        Directory.CreateDirectory(Path.Combine(source, "icons"));
        File.WriteAllText(Path.Combine(source, "manifest.json"), "{\"v\":1}");
        File.WriteAllBytes(Path.Combine(source, "icons", "a.png"), [1, 2, 3]);
        var support = TestFolder.New();
        var copy = Path.Combine(support, "BYOA", "extension");

        Assert.Equal(2, NativeHostManifests.SyncExtension(source, support));
        Assert.Equal("{\"v\":1}", File.ReadAllText(Path.Combine(copy, "manifest.json")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(copy, "icons", "a.png")));
        Assert.Equal(0, NativeHostManifests.SyncExtension(source, support));              // nothing changed: nothing written

        File.WriteAllText(Path.Combine(source, "manifest.json"), "{\"v\":2}");
        File.WriteAllText(Path.Combine(copy, "kept-by-the-person.txt"), "mine");
        Assert.Equal(1, NativeHostManifests.SyncExtension(source, support));              // the changed file follows
        Assert.Equal("{\"v\":2}", File.ReadAllText(Path.Combine(copy, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(copy, "kept-by-the-person.txt")));           // a file of the person's own is left alone
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void OnlyOneHolderOfTheLockAtATimeAndItIsFreeAgainWhenReleased()
    {
        var file = Path.Combine(TestFolder.New(), "sub", "instance.lock");
        var first = SingleInstance.TryLockFile(file);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryLockFile(file));
        first.Dispose();
        using var again = SingleInstance.TryLockFile(file);
        Assert.NotNull(again);
    }
}

public class UnixWalletPathsTests
{
    [UnixFact]
    public void SocketsLiveInAFolderOfTheUsersOwnNeverUnderTmpdirAndFitTheLimit()
    {
        var pipe = WalletPaths.PipeName();
        var mcp = WalletPaths.McpPipeName();
        if (OperatingSystem.IsLinux() && RuntimeFolder() is { } runtime)
        {
            // the Linux session's own runtime folder is the usual place for sockets
            Assert.Equal(Path.Combine(runtime, "byoa", "wallet.sock"), pipe);
            Assert.Equal(Path.Combine(runtime, "byoa", "mcp.sock"), mcp);
        }
        else
        {
            Assert.Equal(Path.Combine(WalletPaths.RunFolder, "wallet.sock"), pipe);
            Assert.Equal(Path.Combine(WalletPaths.RunFolder, "mcp.sock"), mcp);
        }

        Assert.False(pipe.StartsWith(Path.GetTempPath(), StringComparison.Ordinal));
        Assert.True(pipe.Length <= (OperatingSystem.IsLinux() ? 107 : 103) && mcp.Length <= (OperatingSystem.IsLinux() ? 107 : 103));
        Assert.NotEqual(pipe, mcp);
    }

    [UnixFact]
    public void ASeparateDataFolderGetsShortSocketNamesOfItsOwn()
    {
        var home = Path.Combine(TestFolder.New(), new string('x', 80));       // a long data folder must not make a long socket name
        var one = WalletPaths.PipeName(home);
        Assert.StartsWith(Path.GetTempPath(), one);
        Assert.True(one.Length <= 103, one);
        Assert.NotEqual(one, WalletPaths.PipeName(home + "2"));
        Assert.NotEqual(one, WalletPaths.McpPipeName(home));
    }

    [UnixFact]
    public void ThePrepareStepClosesTheFolderToOtherUsersAndRefusesAPathThatIsTooLong()
    {
        Assert.Throws<IOException>(() => WalletPaths.PrepareSocketFolder("/" + new string('a', 120) + ".sock"));
        var folder = Path.Combine(Path.GetTempPath(), "byoa-prep-" + Guid.NewGuid().ToString("N")[..10]);   // short: a socket path is limited
        WalletPaths.PrepareSocketFolder(Path.Combine(folder, "x.sock"));
        Assert.True(Directory.Exists(folder));
    }

    [UnixFact]
    public void TheSocketFileItselfIsClosedToOtherUsers()
    {
        var path = Path.Combine(Path.GetTempPath(), "byoa-m-" + Guid.NewGuid().ToString("N")[..10] + ".sock");   // short: a socket path is limited
        using var server = new NamedPipeServerStream(path, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        WalletPaths.CloseSocketToOtherUsers(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [LinuxFact]
    public void ASocketPathMayBe107CharactersOnLinuxAndTheRuntimeFolderIsClosedToOtherUsers()
    {
        var runtime = Path.Combine(Path.GetTempPath(), "byoa-xdg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(runtime);
        var old = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
        try
        {
            var pipe = WalletPaths.PipeName();
            Assert.Equal(Path.Combine(runtime, "byoa", "wallet.sock"), pipe);
            WalletPaths.PrepareSocketFolder(pipe);
            var folder = Path.Combine(runtime, "byoa");
            Assert.True(Directory.Exists(folder));
            var mode = File.GetUnixFileMode(folder);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", old);
        }
    }

    [UnixFact]
    public async Task AWalletSocketCarriesAFrameBothWaysForItsOwnUserAndReportsTheSameProcessAtTheOtherEnd()
    {
        var path = Path.Combine(Path.GetTempPath(), "byoa-w-" + Guid.NewGuid().ToString("N")[..10] + ".sock");   // short: a socket path is limited
        using var server = new NamedPipeServerStream(path, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", path, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(3000);
        await accepted;
        await Framing.WriteAsync(client, Encoding.UTF8.GetBytes("{\"hello\":1}"));
        Assert.Equal("{\"hello\":1}", Encoding.UTF8.GetString((await Framing.ReadAsync(server))!));

        // the process at the other end is this one: the same path the system reports for the running test host, and its parent
        IAgentProcessInfo info = OperatingSystem.IsMacOS() ? new MacAgentProcessInfo() : new LinuxAgentProcessInfo();
        var seen = info.Read(server.SafePipeHandle);
        Assert.NotNull(seen);
        Assert.Equal((uint)Environment.ProcessId, seen.ClientProcessId);
        Assert.Equal(Process.GetCurrentProcess().MainModule!.FileName, seen.ClientImagePath);
        Assert.False(string.IsNullOrEmpty(seen.ParentImagePath));
        Assert.True(seen.ClientStartTime is { } started && Math.Abs((started - Process.GetCurrentProcess().StartTime).TotalSeconds) < 5);
    }

    static string? RuntimeFolder()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrWhiteSpace(runtime) || !Directory.Exists(runtime) ? null : runtime;
    }
}

public class UnixAgentProgramsTests
{
    [UnixFact]
    public void AProgramIsFoundByNameInTheUsualFoldersOnlyWhenItCanBeRun()
    {
        var sh = AgentPrograms.Resolve("sh");
        Assert.NotNull(sh);
        Assert.EndsWith("/sh", sh);
        Assert.Null(AgentPrograms.Resolve("no-such-agent-program-0123"));

        var dir = TestFolder.New();
        var plain = Path.Combine(dir, "not-runnable");
        File.WriteAllText(plain, "#!/bin/sh\n");
        Assert.Null(AgentPrograms.Resolve("not-runnable"));                    // not on PATH anyway
        Assert.Equal(plain, AgentPrograms.Resolve(plain));                     // a full path is the person's own choice
    }

    [MacFact]
    public void OnAMacTheSearchIncludesTheFoldersAnAppStartedFromTheDockDoesNotHave()
    {
        var all = AgentPrograms.SearchDirectories();
        Assert.Contains("/opt/homebrew/bin", all);
        Assert.Contains("/usr/local/bin", all);
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"), all);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Contains("/opt/homebrew/bin", AgentPrograms.SearchPath().Split(':'));
    }

    [Theory]
    [InlineData("/Applications/Agent Wallet.app/Contents/MacOS/", "/Applications/Agent Wallet.app/Contents/MacOS/byoa-mcp", true)]
    [InlineData("/Applications/Agent Wallet.app/Contents/MacOS", "/Applications/Agent Wallet.app/Contents/MacOS/byoa-mcp", true)]
    [InlineData("/Applications/Agent Wallet.app/Contents/MacOS/", "/Applications/Other.app/Contents/MacOS/byoa-mcp", false)]
    [InlineData("/Applications/Agent Wallet.app/Contents/MacOS/", "/Applications/Agent Wallet.app/Contents/MacOS/BYOA-MCP", false)]
    [InlineData("/Applications/Agent Wallet.app/Contents/MacOS/", "/Applications/Agent Wallet.app/Contents/MacOS/sub/byoa-mcp", false)]
    [InlineData("/Applications/Agent Wallet.app/Contents/MacOS/", "/tmp/byoa-mcp", false)]
    [InlineData("/Applications/Agent Wallet.app/Contents/MacOS/", "byoa-mcp", false)]
    public void OnAMacTheWalletRecognisesItsOwnBridgeOnlyInItsOwnFolder(string walletFolder, string clientImage, bool expected)
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, AgentPipeServer.IsBridgeIn(clientImage, walletFolder));
    }

    [Theory]
    [InlineData("/opt/homebrew/bin/python3.12", true)]
    [InlineData("/usr/bin/python3", true)]
    [InlineData("/bin/zsh", true)]
    [InlineData("/opt/homebrew/bin/node", true)]
    [InlineData("/Users/v/.local/bin/claude", false)]
    [InlineData("/Applications/Agent Wallet.app/Contents/MacOS/byoa-mcp", false)]
    public void ShellsAndInterpretersAreRecognisedByTheirNames(string path, bool interpreter)
    {
        Assert.Equal(interpreter, Byoa.Wallet.Registry.AgentIdentity.IsInterpreterPath(path));
    }
}

public class MacKeychainTests
{
    [MacFact]
    public void ARealKeychainFileOfTheTestsOwnKeepsAnItemAcrossInstancesAndNeverTouchesTheLoginKeychain()
    {
        var dir = TestFolder.New();
        var keychain = Path.Combine(dir, "test.keychain-db");
        MacKeychainItem.CreateKeychainFile(keychain, Guid.NewGuid().ToString("N"));
        Assert.Throws<IOException>(() => MacKeychainItem.CreateKeychainFile(keychain, "again"));    // never over an existing file

        var item = new MacKeychainItem("org.bringyourownagent.wallet.test", "unit-test-" + Guid.NewGuid().ToString("N"), "BYOA unit test", keychain);
        Assert.Null(item.Get());
        var key = RandomNumberGenerator.GetBytes(32);
        item.Set(key);
        Assert.Equal(key, item.Get());
        var changed = RandomNumberGenerator.GetBytes(32);
        item.Set(changed);                                                                           // replaced, not duplicated
        Assert.Equal(changed, item.Get());

        // the whole chain with this item: sealing a file under a key held by the real Keychain code
        var protector = new KeychainSecretProtector(new MacKeychainItem("org.bringyourownagent.wallet.test", "seal-" + Guid.NewGuid().ToString("N"), "BYOA unit test", keychain));
        var sealedBytes = protector.Protect(Encoding.UTF8.GetBytes("{\"k\":\"v\"}"));
        Assert.Equal("{\"k\":\"v\"}", Encoding.UTF8.GetString(protector.Unprotect(sealedBytes)));

        Assert.Throws<KeychainException>(() => new MacKeychainItem("s", "a", "l", Path.Combine(dir, "missing.keychain-db")).Get());
    }
}
