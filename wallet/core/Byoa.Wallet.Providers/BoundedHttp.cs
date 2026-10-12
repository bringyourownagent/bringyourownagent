using System.Text;

namespace Byoa.Wallet.Providers;

/// <summary>Reads small JSON responses without letting a local or remote server choose the allocation size.</summary>
internal static class BoundedHttp
{
    /// <summary>Enough for a model list or a completion, while still a small upper bound for a tray process.</summary>
    public const int MaxJsonBytes = 4 * 1024 * 1024;

    public static async Task<string> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct,
        int maxBytes = MaxJsonBytes)
    {
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw TooLarge(maxBytes);

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            if (read > maxBytes - total) throw TooLarge(maxBytes);
            output.Write(buffer, 0, read);
            total += read;
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    static HttpRequestException TooLarge(int maxBytes) =>
        new("response body is larger than " + maxBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes");
}
