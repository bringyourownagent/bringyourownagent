using System.Net;
using System.Security.Cryptography;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

public sealed class GigaAmSourceTests
{
    static (IReadOnlyList<GigaAmFileSpec> Specs, Dictionary<string, byte[]> Files) SmallFiles()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["encoder.int8.onnx"] = [1, 2, 3, 4],
            ["decoder.onnx"] = [5, 6],
            ["joiner.onnx"] = [7, 8, 9],
            ["tokens.txt"] = "0 test\n"u8.ToArray(),
            ["LICENSE"] = "test license\n"u8.ToArray(),
        };
        var specs = files.Select(pair => new GigaAmFileSpec(pair.Key, pair.Value.Length,
            Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant())).ToArray();
        return (specs, files);
    }

    static GigaAmSource Source(string root, FakeDownloadHandler handler, IReadOnlyList<GigaAmFileSpec> specs)
    {
        var http = new HttpClient(handler);
        return new GigaAmSource(() => null, http, root, new Uri("http://giga.test/"), specs);
    }

    [Fact]
    public async Task DownloadsEveryPinnedFileIntoAnAtomicWalletFolder()
    {
        var (specs, files) = SmallFiles();
        var root = TestFolder.New();
        using var handler = new FakeDownloadHandler(files);
        using var source = Source(root, handler, specs);
        var progress = new List<double?>();
        source.ProgressChanged += () => progress.Add(source.DownloadProgress);

        Assert.Equal(SourceState.NeedsDownload, source.State);
        await source.EnsureDownloadedAsync(CancellationToken.None);

        var folder = Path.Combine(root, "gigaam", GigaAmSource.ModelFolderName);
        Assert.Equal(SourceState.Ready, source.State);
        Assert.Equal(folder, source.Folder);
        Assert.True(File.Exists(Path.Combine(folder, GigaAmSource.OwnershipMarker)));
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(folder, file.Key)));
        Assert.False(Directory.Exists(folder + ".part"));
        Assert.Null(source.DownloadProgress);
        Assert.Contains(progress, value => value is 0);
        Assert.Contains(progress, value => value is >= 0.99);
        Assert.Equal(5, handler.Requests);
    }

    [Fact]
    public async Task ACorruptFileLeavesNoFinalFolderOrStagingFolder()
    {
        var (specs, files) = SmallFiles();
        var bad = specs.Select(file => file.Name == "decoder.onnx"
            ? file with { Sha256 = new string('0', 64) }
            : file).ToArray();
        var root = TestFolder.New();
        using var source = Source(root, new FakeDownloadHandler(files), bad);

        await source.EnsureDownloadedAsync(CancellationToken.None);

        Assert.Equal(SourceState.Broken, source.State);
        Assert.Contains("SHA-256", source.StateText, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(root, "gigaam", GigaAmSource.ModelFolderName)));
        Assert.False(Directory.Exists(Path.Combine(root, "gigaam", GigaAmSource.ModelFolderName + ".part")));
    }

    [Fact]
    public async Task CancellationDuringDownloadCleansTheOwnedStagingFolder()
    {
        var (specs, files) = SmallFiles();
        var root = TestFolder.New();
        using var handler = new FakeDownloadHandler(files) { Block = true };
        using var source = Source(root, handler, specs);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.EnsureDownloadedAsync(cancelled.Token));
        for (var i = 0; i < 20 && source.State == SourceState.Downloading; i++)
            await Task.Delay(10);

        Assert.Equal(SourceState.NeedsDownload, source.State);
        Assert.False(Directory.Exists(Path.Combine(root, "gigaam", GigaAmSource.ModelFolderName)));
        Assert.False(Directory.Exists(Path.Combine(root, "gigaam", GigaAmSource.ModelFolderName + ".part")));
    }

    [Fact]
    public async Task AVisitorFolderIsReadyAndIsNeverDownloadedOrChanged()
    {
        var root = TestFolder.New();
        var folder = Path.Combine(root, "visitor-giga-am-model");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "encoder.int8.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt" })
            File.WriteAllBytes(Path.Combine(folder, name), [1]);
        var before = Directory.GetFiles(folder).ToDictionary(path => path, File.ReadAllBytes);
        using var handler = new FakeDownloadHandler(new Dictionary<string, byte[]>(), block: false);
        using var source = new GigaAmSource(() => folder, new HttpClient(handler), Path.Combine(root, "wallet-models"));

        Assert.Equal(SourceState.Ready, source.State);
        await source.EnsureDownloadedAsync(CancellationToken.None);

        Assert.Equal(0, handler.Requests);
        Assert.Equal(before.Count, Directory.GetFiles(folder).Length);
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Fact]
    public void ProductionCatalogIsThePublishedRussianModel()
    {
        Assert.Equal(229_344_179, GigaAmSource.DownloadFiles.Sum(file => file.SizeBytes));
        Assert.Contains("giga-am", GigaAmSource.ModelFolderName, StringComparison.OrdinalIgnoreCase);
        Assert.All(GigaAmSource.DownloadFiles, file => Assert.Matches("^[0-9a-f]{64}$", file.Sha256));
    }

    sealed class FakeDownloadHandler : HttpMessageHandler, IDisposable
    {
        readonly IReadOnlyDictionary<string, byte[]> _files;
        readonly bool _block;
        public FakeDownloadHandler(IReadOnlyDictionary<string, byte[]> files, bool block = false)
        {
            _files = files;
            _block = block;
        }

        public bool Block { get; set; }
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (_block || Block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var name = request.RequestUri?.AbsolutePath.Trim('/');
            if (name is null || !_files.TryGetValue(name, out var data))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(data) };
            response.Content.Headers.ContentLength = data.Length;
            return response;
        }
    }
}
