using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Byoa.Wallet.Desktop;

namespace Byoa.Wallet.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("0.30.1", "0.30.2", -1)]
    [InlineData("0.31.0", "0.30.99", 1)]
    [InlineData("1.0.0", "0.99.99", 1)]
    [InlineData("0.30.1", "0.30.1", 0)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-beta.11", "1.0.0-beta.2", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0.0", "1.0.0-beta", 1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta", 1)]
    [InlineData("1.0.0+build.7", "1.0.0", 0)]
    public void VersionsAreComparedByTheSemanticRules(string left, string right, int expected)
    {
        Assert.True(SemanticVersion.TryParse(left, out var a));
        Assert.True(SemanticVersion.TryParse(right, out var b));
        Assert.Equal(expected, Math.Sign(a.CompareTo(b)));
        Assert.Equal(-Math.Sign(expected), Math.Sign(b.CompareTo(a)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("one.two.three")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.-2.3")]
    public void AVersionThatIsNotSemanticIsRefused(string text) => Assert.False(SemanticVersion.TryParse(text, out _));
}

public class LinuxUpdateEngineTests
{
    const string Feed = "http://updates.test";
    const UnixFileMode Read = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    const UnixFileMode Run = Read | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    const UnixFileMode Folder = Run | UnixFileMode.UserExecute;

    sealed class FakeSite : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>A length the server claims for a path, when the body is shorter on purpose (an interrupted download).</summary>
        public Dictionary<string, long> ClaimedLengths { get; } = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Asked.Add(path);
            if (!Files.TryGetValue(path, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var content = new ByteArrayContent(body);
            if (ClaimedLengths.TryGetValue(path, out var claimed)) content.Headers.ContentLength = claimed;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    sealed record Layout(LinuxUpdateEngine Engine, LinuxUpdateOptions Options, FakeSite Site, string DataRoot, string OldVersion, string Current)
    {
        public string Versions => Path.Combine(DataRoot, "AgentWallet", "versions");
        public string Downloads => Path.Combine(DataRoot, "AgentWallet", "downloads");
    }

    static Layout Make(string runningVersion = "0.30.1", string? offeredVersion = null, byte[]? package = null,
        string? sha256 = null, long? size = null, bool installed = true, bool runningInside = false, Action<string, int>? restarted = null)
    {
        var dataRoot = TestFolder.New();
        var old = Path.Combine(dataRoot, "AgentWallet", "versions", runningVersion);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "Byoa.Wallet"), "old " + runningVersion);
        var current = Path.Combine(dataRoot, "AgentWallet", "current");
        if (installed) Directory.CreateDirectory(current);                      // the updater only asks whether it is there

        var site = new FakeSite();
        if (offeredVersion is not null)
        {
            package ??= Package(offeredVersion);
            var file = "byoa-wallet-" + offeredVersion + "-linux-x64.tar.gz";
            site.Files["/latest.json"] = Encoding.UTF8.GetBytes("{\"version\":\"" + offeredVersion + "\",\"file\":\"" + file
                + "\",\"sha256\":\"" + (sha256 ?? Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant())
                + "\",\"size\":" + (size ?? package.Length) + "}");
            site.Files["/" + file] = package;
        }

        var options = new LinuxUpdateOptions
        {
            Http = new HttpClient(site),
            FeedBase = Feed,
            DataRoot = dataRoot,
            RunningFolder = installed || runningInside ? old : dataRoot,
            RunningVersion = runningVersion,
            ProcessId = 4242,
            StartRestarted = restarted ?? ((_, _) => { }),
        };
        return new Layout(new LinuxUpdateEngine(options), options, site, dataRoot, old, current);
    }

    static byte[] Package(string version, string? extraName = null, TarEntryType extraType = TarEntryType.RegularFile, string? linkName = null)
    {
        var top = "byoa-wallet-" + version + "-linux-x64";
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top) { Mode = Folder });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/app") { Mode = Folder });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/extension") { Mode = Folder });
            writer.WriteEntry(FileEntry(top + "/app/Byoa.Wallet", "wallet " + version, Run));
            writer.WriteEntry(FileEntry(top + "/app/byoa-host", "host " + version, Run));
            writer.WriteEntry(FileEntry(top + "/app/byoa-mcp", "mcp " + version, Run));
            writer.WriteEntry(FileEntry(top + "/app/libSkiaSharp.so", "library", Read));
            writer.WriteEntry(FileEntry(top + "/extension/manifest.json", "{\"name\":\"test\"}", Read));
            writer.WriteEntry(FileEntry(top + "/README.txt", "for the person", Read));
            if (extraName is not null)
            {
                var entry = extraType == TarEntryType.SymbolicLink
                    ? new PaxTarEntry(TarEntryType.SymbolicLink, top + "/" + extraName) { LinkName = linkName, Mode = Run }
                    : FileEntry(top + "/" + extraName, "planted", Run);
                writer.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    static PaxTarEntry FileEntry(string name, string text, UnixFileMode mode) =>
        new(TarEntryType.RegularFile, name) { Mode = mode, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) };

    static string Text(string path) => File.ReadAllText(path);

    [Fact]
    public void OnlyACopyInsideVersionsWithCurrentBesideItCanUpdateItself()
    {
        var l = Make();
        Assert.True(l.Engine.CanUpdateItself);

        var elsewhere = new LinuxUpdateEngine(l.Options with { RunningFolder = l.DataRoot });
        Assert.False(elsewhere.CanUpdateItself);

        Directory.CreateDirectory(l.Current);                                   // what install.sh leaves; here a plain folder is enough
        var otherData = TestFolder.New();
        var version = Path.Combine(otherData, "AgentWallet", "versions", "0.30.1");
        Directory.CreateDirectory(version);
        var noCurrent = new LinuxUpdateEngine(l.Options with { DataRoot = otherData, RunningFolder = version });
        Assert.False(noCurrent.CanUpdateItself);
    }

    [Fact]
    public async Task ALowerOrEqualVersionIsNotOfferedAndAHigherOneIs()
    {
        foreach (var offered in new[] { "0.30.0", "0.30.1", "0.30.1-beta.2", "0.29.99" })
        {
            var l = Make(offeredVersion: offered);
            Assert.Null(await l.Engine.CheckAsync(default));
        }

        var higher = Make(offeredVersion: "0.30.2");
        Assert.Equal("0.30.2", await higher.Engine.CheckAsync(default));
        Assert.Contains("/latest.json", higher.Site.Asked);
    }

    [Fact]
    public async Task AWrongSha256StopsTheDownloadAndLeavesTheRunningVersionAlone()
    {
        var package = Package("0.30.2");
        var l = Make(offeredVersion: "0.30.2", package: package, sha256: new string('0', 64));
        Assert.Equal("0.30.2", await l.Engine.CheckAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => l.Engine.DownloadAsync(_ => { }, default));

        Assert.Equal("old 0.30.1", Text(Path.Combine(l.OldVersion, "Byoa.Wallet")));
        Assert.False(Directory.Exists(Path.Combine(l.Versions, "0.30.2")));
        Assert.False(Directory.Exists(Path.Combine(l.Versions, "0.30.2.tmp")));
        Assert.False(File.Exists(Path.Combine(l.Versions, "0.30.2.tmp", "Byoa.Wallet")));
        Assert.True(File.Exists(Path.Combine(l.Downloads, "byoa-wallet-0.30.2-linux-x64.tar.gz.part")));   // the failed download stays for inspection
    }

    [Fact]
    public async Task AnInterruptedDownloadLeavesTheCurrentVersionAlone()
    {
        var package = Package("0.30.2");
        var l = Make(offeredVersion: "0.30.2", package: package, size: package.Length);
        var file = "byoa-wallet-0.30.2-linux-x64.tar.gz";
        l.Site.Files["/" + file] = package[..4];                                // four bytes, then the connection ends
        l.Site.ClaimedLengths["/" + file] = package.Length;                     // while the server claimed the whole package

        Assert.Equal("0.30.2", await l.Engine.CheckAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => l.Engine.DownloadAsync(_ => { }, default));

        Assert.Equal("old 0.30.1", Text(Path.Combine(l.OldVersion, "Byoa.Wallet")));
        Assert.False(Directory.Exists(Path.Combine(l.Versions, "0.30.2")));
        Assert.False(Directory.Exists(Path.Combine(l.Versions, "0.30.2.tmp")));
    }

    [Fact]
    public async Task AnArchiveThatTriesToLeaveTheVersionFolderIsRefused()
    {
        var package = Package("0.30.2", extraName: "app/../../escape.txt");
        var l = Make(offeredVersion: "0.30.2", package: package);
        Assert.Equal("0.30.2", await l.Engine.CheckAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => l.Engine.DownloadAsync(_ => { }, default));

        Assert.False(File.Exists(Path.Combine(l.Versions, "escape.txt")));
        Assert.False(Directory.Exists(Path.Combine(l.Versions, "0.30.2")));
        Assert.Equal("old 0.30.1", Text(Path.Combine(l.OldVersion, "Byoa.Wallet")));
    }

    [Fact]
    public async Task AnArchiveWithALinkIsRefused()
    {
        var package = Package("0.30.2", extraName: "app/escape", extraType: TarEntryType.SymbolicLink, linkName: "../../../etc/passwd");
        var l = Make(offeredVersion: "0.30.2", package: package);
        Assert.Equal("0.30.2", await l.Engine.CheckAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => l.Engine.DownloadAsync(_ => { }, default));

        Assert.False(File.Exists(Path.Combine(l.Versions, "0.30.2.tmp", "escape")));
    }

    [Fact]
    public void AnArchiveWithoutTheProgramsIsRefused()
    {
        var archive = Path.Combine(TestFolder.New(), "package.tar.gz");
        File.WriteAllBytes(archive, Package("0.30.2"));
        var unpacked = Path.Combine(TestFolder.New(), "version");
        LinuxUpdatePackage.Extract(archive, unpacked);
        LinuxUpdatePackage.CheckPrograms(unpacked);                             // the package above is complete

        var empty = Path.Combine(TestFolder.New(), "empty");
        Directory.CreateDirectory(empty);
        Assert.Throws<InvalidDataException>(() => LinuxUpdatePackage.CheckPrograms(empty));
    }

    [Fact]
    public void AnEntryWithAnEmptyOrParentSegmentOrNoEntriesAtAllIsRefused()
    {
        var notPlain = Path.Combine(TestFolder.New(), "not-plain.tar.gz");
        File.WriteAllBytes(notPlain, Package("0.30.2", extraName: "app//etc/passwd"));
        Assert.Throws<InvalidDataException>(() => LinuxUpdatePackage.Extract(notPlain, Path.Combine(TestFolder.New(), "v")));

        var parent = Path.Combine(TestFolder.New(), "parent.tar.gz");
        File.WriteAllBytes(parent, Package("0.30.2", extraName: "app/../../escape.txt"));
        Assert.Throws<InvalidDataException>(() => LinuxUpdatePackage.Extract(parent, Path.Combine(TestFolder.New(), "v")));

        var empty = Path.Combine(TestFolder.New(), "empty.tar.gz");
        using (var buffer = new MemoryStream())
        {
            using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            using (new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true)) { }
            File.WriteAllBytes(empty, buffer.ToArray());
        }

        Assert.Throws<InvalidDataException>(() => LinuxUpdatePackage.Extract(empty, Path.Combine(TestFolder.New(), "v")));
    }

    [UnixFact]
    public void AnUpdateIsUnpackedIntoVersionsSwappedInAndStarted()
    {
        var package = Package("0.30.2");
        var restarted = new List<(string Root, int ProcessId)>();
        var l = Make(offeredVersion: "0.30.2", package: package, installed: false, runningInside: true, restarted: (root, pid) => restarted.Add((root, pid)));
        Directory.CreateSymbolicLink(l.Current, l.OldVersion);                   // what install.sh leaves: current -> versions/0.30.1
        Assert.True(l.Engine.CanUpdateItself);

        Assert.Equal("0.30.2", l.Engine.CheckAsync(default).GetAwaiter().GetResult());
        l.Engine.DownloadAsync(_ => { }, default).GetAwaiter().GetResult();
        Assert.False(Directory.Exists(Path.Combine(l.Versions, "0.30.2")));     // unpacked under .tmp, not in place yet
        Assert.True(File.Exists(Path.Combine(l.Versions, "0.30.2.tmp", "Byoa.Wallet")));
        Assert.Equal("old 0.30.1", Text(Path.Combine(l.OldVersion, "Byoa.Wallet")));

        l.Engine.ApplyAndRestart();

        var installed = Path.Combine(l.Versions, "0.30.2");
        Assert.True(Directory.Exists(installed));
        Assert.False(Directory.Exists(installed + ".tmp"));
        Assert.Equal(installed, new DirectoryInfo(l.Current).LinkTarget);
        Assert.Equal("wallet 0.30.2", Text(Path.Combine(installed, "Byoa.Wallet")));
        Assert.True(File.Exists(Path.Combine(installed, "byoa-host")));
        Assert.True(File.Exists(Path.Combine(installed, "byoa-mcp")));
        Assert.True(File.Exists(Path.Combine(installed, "extension", "manifest.json")));
        Assert.False(File.Exists(Path.Combine(installed, "README.txt")));        // the installer's readme is not part of a version
        foreach (var program in LinuxUpdatePackage.Programs)
            Assert.True((File.GetUnixFileMode(Path.Combine(installed, program)) & UnixFileMode.UserExecute) != 0, program);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(Path.Combine(installed, "libSkiaSharp.so")) & ~UnixFileMode.UserWrite);
        Assert.Equal([(Path.Combine(l.DataRoot, "AgentWallet"), 4242)], restarted);
        Assert.Equal("old 0.30.1", Text(Path.Combine(l.OldVersion, "Byoa.Wallet")));   // the old version stays
    }

    [LinuxFact]
    public void TheRestartHelperWaitsForTheOldProcessThenStartsCurrent()
    {
        var root = Path.Combine(TestFolder.New(), "AgentWallet");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        var marker = Path.Combine(root, "started.txt");
        var wallet = Path.Combine(current, "Byoa.Wallet");
        File.WriteAllText(wallet, "#!/bin/sh\necho started > " + LinuxRestart.ShellQuote(marker) + "\n");
        File.SetUnixFileMode(wallet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var old = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "0.4" }, UseShellExecute = false })!;
        LinuxRestart.StartDetached(root, old.Id);
        old.WaitForExit();                                         // reaped: kill -0 now says the process is gone

        for (var tries = 0; tries < 100 && !File.Exists(marker); tries++) Thread.Sleep(100);
        Assert.True(File.Exists(marker), "the helper did not start current/Byoa.Wallet after the old process ended");
    }

    [UnixFact]
    public void ASecondApplyWithoutADownloadIsRefused()
    {
        var l = Make(offeredVersion: "0.30.2", installed: false);
        Directory.CreateSymbolicLink(l.Current, l.OldVersion);
        Assert.Equal("0.30.2", l.Engine.CheckAsync(default).GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => l.Engine.ApplyAndRestart());
    }

    [Fact]
    public async Task AFeedThatIsNotJsonOrNamesAnotherFolderIsRefused()
    {
        var l = Make(offeredVersion: "0.30.2");
        l.Site.Files["/latest.json"] = Encoding.UTF8.GetBytes("not json");
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => l.Engine.CheckAsync(default));

        var traversal = Make(offeredVersion: "0.30.2");
        traversal.Site.Files["/latest.json"] = Encoding.UTF8.GetBytes("{\"version\":\"0.30.2\",\"file\":\"../../evil.tar.gz\",\"sha256\":\""
            + new string('a', 64) + "\",\"size\":1}");
        await Assert.ThrowsAsync<InvalidDataException>(() => traversal.Engine.CheckAsync(default));
    }
}