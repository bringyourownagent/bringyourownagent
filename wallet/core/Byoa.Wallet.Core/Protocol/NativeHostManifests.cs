using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Byoa.Wallet.Protocol;

/// <summary>
/// The native messaging host registration of the wallet's browser extension on macOS. Chromium browsers find a native host by a small JSON
/// file in <c>~/Library/Application Support/&lt;browser&gt;/NativeMessagingHosts/</c> (Windows keeps the same thing in the registry; install.ps1
/// writes it there). The wallet writes its own file for every installed browser that has a folder there, each time it starts: a copy that
/// moved, or a browser installed later, is picked up without an installer.
/// </summary>
public static class NativeHostManifests
{
    public const string HostName = "org.bringyourownagent.wallet";

    /// <summary>A variable that points the registration at another "Application Support" folder (tests and tries never touch the person's browsers).</summary>
    public const string RootVariable = "BYOA_NATIVE_HOST_ROOT";

    /// <summary>A browser's data folder under <c>Application Support</c>, as Chromium browsers name it.</summary>
    public sealed record Browser(string Name, string Folder);

    public static readonly IReadOnlyList<Browser> MacBrowsers =
    [
        new("Google Chrome", "Google/Chrome"),
        new("Google Chrome Beta", "Google/Chrome Beta"),
        new("Google Chrome Canary", "Google/Chrome Canary"),
        new("Chromium", "Chromium"),
        new("Microsoft Edge", "Microsoft Edge"),
        new("Brave", "BraveSoftware/Brave-Browser"),
        new("Vivaldi", "Vivaldi"),
        new("Arc", "Arc/User Data"),
        new("Opera", "com.operasoftware.Opera"),
    ];

    /// <summary>
    /// The ID Chrome gives an extension from the public key in its manifest: the first 32 hex digits of the key's SHA-256, written with the
    /// letters a to p (the same as install.ps1 computes).
    /// </summary>
    public static string ExtensionId(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var key = document.RootElement.TryGetProperty("key", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("the extension's manifest has no \"key\": its ID cannot be worked out");
        var hash = SHA256.HashData(Convert.FromBase64String(key));
        var id = new StringBuilder(32);
        foreach (var b in hash.AsSpan(0, 16))
        {
            id.Append((char)('a' + (b >> 4)));
            id.Append((char)('a' + (b & 0xF)));
        }

        return id.ToString();
    }

    /// <summary>The text of a host manifest: the program Chrome starts, and the one extension allowed to talk to it.</summary>
    public static string ManifestJson(string hostPath, string extensionId)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("name", HostName);
            json.WriteString("description", "BYOA agent wallet: lets pages use your own AI within your limits");
            json.WriteString("path", hostPath);
            json.WriteString("type", "stdio");
            json.WriteStartArray("allowed_origins");
            json.WriteStringValue("chrome-extension://" + extensionId + "/");
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>The "Application Support" folder: the person's own, or the one <see cref="RootVariable"/> names.</summary>
    public static string ApplicationSupport()
    {
        var custom = Environment.GetEnvironmentVariable(RootVariable);
        return string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Path.GetFullPath(custom);
    }

    /// <summary>
    /// Writes the host manifest for each browser whose folder exists under <paramref name="applicationSupport"/>; a file that already says
    /// the same is left as it is. Nothing is created for a browser that is not installed, and nothing is deleted.
    /// </summary>
    /// <returns>For each browser it wrote or checked: its name and what happened ("written", "up to date").</returns>
    public static IReadOnlyList<(string Browser, string Result)> Register(string hostPath, string extensionManifestJson, string applicationSupport)
    {
        var manifest = ManifestJson(hostPath, ExtensionId(extensionManifestJson));
        var results = new List<(string, string)>();
        foreach (var browser in MacBrowsers)
        {
            var folder = Path.Combine(applicationSupport, browser.Folder);
            if (!Directory.Exists(folder)) continue;
            var target = Path.Combine(folder, "NativeMessagingHosts", HostName + ".json");
            if (File.Exists(target) && File.ReadAllText(target, Encoding.UTF8) == manifest)
            {
                results.Add((browser.Name, "up to date"));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".tmp";
            File.WriteAllText(temp, manifest, new UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);
            results.Add((browser.Name, "written"));
        }

        return results;
    }
}
