using System.Text.Json.Nodes;

namespace Byoa.Wallet.Protocol;

/// <summary>
/// The frames that carry the pictures of an <c>image.generate</c> answer (PROPOSAL-v3 3.2), the same on the direct link and on the native host's
/// pipe: each picture goes as one or more <c>image</c> frames, then the <c>result</c> that describes them.
/// <code>{"type":"image","id":7,"index":0,"seq":0,"of":3,"mime":"image/png","data":"&lt;base64&gt;"}</code>
/// </summary>
public static class PictureFrames
{
    /// <summary>The most base64 characters one part carries; a frame stays well under the 1 MB Chrome takes from a native host.</summary>
    public const int PartChars = 524_288;

    const int PartBytes = PartChars / 4 * 3;

    /// <summary>The frames of the pictures an answer describes, in the order of its <c>output.images</c>: each picture in parts, the parts in order.</summary>
    /// <param name="id">The call's id, as the site gave it.</param>
    /// <param name="result">The answer; its <c>output.images[i].mime</c> is the type of <paramref name="pictures"/>[i].</param>
    public static IEnumerable<JsonObject> Of(JsonNode id, JsonObject result, IReadOnlyList<byte[]> pictures)
    {
        if (result["output"]?["images"] is not JsonArray images) yield break;
        for (var index = 0; index < pictures.Count && index < images.Count; index++)
        {
            var mime = images[index]?["mime"]?.GetValue<string>() ?? "";
            var data = pictures[index];
            var parts = Math.Max(1, (data.Length + PartBytes - 1) / PartBytes);
            for (var seq = 0; seq < parts; seq++)
            {
                var from = seq * PartBytes;
                yield return new JsonObject
                {
                    ["type"] = Wire.Image,
                    ["id"] = id.DeepClone(),
                    ["index"] = index,
                    ["seq"] = seq,
                    ["of"] = parts,
                    ["mime"] = mime,
                    ["data"] = Convert.ToBase64String(data.AsSpan(from, Math.Min(PartBytes, data.Length - from))),
                };
            }
        }
    }

    /// <summary>Whether an answer is one that comes with pictures: a success of a call that drew some.</summary>
    public static bool Carries(JsonNode? result, ICollection<byte[]> pictures) =>
        pictures.Count > 0 && result is JsonObject { } answer && answer["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var succeeded) && succeeded;
}
