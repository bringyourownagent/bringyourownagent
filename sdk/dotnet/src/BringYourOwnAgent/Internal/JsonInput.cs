using System;
using System.Text.Json;

namespace BringYourOwnAgent.Internal;

/// <summary>Documents given as JSON text.</summary>
internal static class JsonInput
{
    private static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = 1000,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>
    /// Parse strict JSON text. A null string gives an undefined element (an absent document).
    /// Returns false when the text is not JSON. The element does not depend on a live JsonDocument.
    /// </summary>
    public static bool TryParse(string? text, out JsonElement element)
    {
        element = default;
        if (text is null)
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(text, Options);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Text that cannot be encoded as UTF-8 (a lone surrogate written as a raw character).
            return false;
        }
    }
}
