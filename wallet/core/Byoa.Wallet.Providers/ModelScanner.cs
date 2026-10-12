using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Providers;

/// <summary>A model found on this computer.</summary>
/// <param name="Kind">"gigaam" (a sherpa-onnx GigaAM folder) or "ggml" (a whisper.cpp model file).</param>
public sealed record FoundModel(string Kind, string Path, string Name, long Bytes);

/// <summary>
/// Finds local speech models in the visitor's model folders. Read only: it never changes anything it finds.
/// </summary>
public static class ModelScanner
{
    const int MaxDepth = 4;

    /// <summary>
    /// The folders a new wallet searches: its own models folder, plus the usual places people keep models
    /// (<c>&lt;drive&gt;:\AI-MODELS</c> and <c>&lt;drive&gt;:\models</c> on fixed drives, <c>%USERPROFILE%\models</c>) when they exist.
    /// </summary>
    public static List<string> DefaultFolders()
    {
        var folders = new List<string> { WalletPaths.Models };
        var candidates = new List<string> { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "models") };
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                candidates.Add(System.IO.Path.Combine(drive.RootDirectory.FullName, "AI-MODELS"));
                candidates.Add(System.IO.Path.Combine(drive.RootDirectory.FullName, "models"));
            }
            catch (IOException)
            {
                // a drive that is not ready
            }
        }

        folders.AddRange(candidates.Where(Directory.Exists));
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<FoundModel> Scan(IEnumerable<string> folders)
    {
        var found = new List<FoundModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (Directory.Exists(folder)) Walk(folder, 0, found, seen);
        }

        return found;
    }

    static void Walk(string dir, int depth, List<FoundModel> found, HashSet<string> seen)
    {
        if (!seen.Add(dir)) return;
        try
        {
            if (IsGigaAm(dir))
            {
                var bytes = Directory.EnumerateFiles(dir, "*.onnx").Sum(f => new FileInfo(f).Length);
                found.Add(new FoundModel("gigaam", dir, System.IO.Path.GetFileName(dir), bytes));
                return;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "ggml-*.bin"))
            {
                var name = System.IO.Path.GetFileName(file);
                if (name.Contains("silero", StringComparison.OrdinalIgnoreCase) || name.Contains("vad", StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(new FoundModel("ggml", file, name, new FileInfo(file).Length));
            }

            if (depth >= MaxDepth) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = System.IO.Path.GetFileName(sub);
                if (name.StartsWith('.') || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("espeak-ng-data", StringComparison.OrdinalIgnoreCase)) continue;
                Walk(sub, depth + 1, found, seen);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // folders we may not read are skipped
        }
    }

    /// <summary>A sherpa-onnx NeMo transducer folder of GigaAM: encoder, decoder, joiner and tokens.</summary>
    public static bool IsGigaAm(string dir) =>
        System.IO.Path.GetFileName(dir).Contains("giga-am", StringComparison.OrdinalIgnoreCase)
        && GigaAmFiles.Find(dir) is not null;
}

/// <summary>The four files of a GigaAM transducer (int8 variants preferred).</summary>
public sealed record GigaAmFiles(string Encoder, string Decoder, string Joiner, string Tokens)
{
    public static GigaAmFiles? Find(string dir)
    {
        string? Pick(string stem)
        {
            var int8 = Path.Combine(dir, stem + ".int8.onnx");
            if (File.Exists(int8)) return int8;
            var plain = Path.Combine(dir, stem + ".onnx");
            return File.Exists(plain) ? plain : null;
        }

        var tokens = Path.Combine(dir, "tokens.txt");
        var (e, d, j) = (Pick("encoder"), Pick("decoder"), Pick("joiner"));
        return e is null || d is null || j is null || !File.Exists(tokens) ? null : new GigaAmFiles(e, d, j, tokens);
    }
}
