using System.Text.Json.Nodes;

namespace Byoa.Wallet.Engine;

/// <summary>One skill a manifest asks for.</summary>
public sealed record RequestedSkill(string Id, bool Required, IReadOnlyList<string> Languages);

/// <summary>
/// The parts of a manifest (already valid under spec/rules-0.1-draft.md) the wallet uses: the purpose, the skills
/// with their languages, the privacy class and the expected cost per session.
/// </summary>
public sealed record RequestedManifest(string Json, string Summary, IReadOnlyList<RequestedSkill> Skills, string Privacy, decimal? ExpectedPerSession)
{
    public static RequestedManifest From(JsonObject manifest)
    {
        var summary = manifest["purpose"]?["summary"]?.GetValue<string>()?.Trim() ?? "";
        var skills = new List<RequestedSkill>();
        if (manifest["skills"] is JsonObject s)
        {
            foreach (var (id, value) in s.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var required = value?["required"] is JsonValue r && r.TryGetValue<bool>(out var b) && b;
                var languages = new List<string>();
                if (value?["languages"] is JsonArray langs)
                {
                    foreach (var l in langs)
                    {
                        if (l is JsonValue lv && lv.TryGetValue<string>(out var tag) && Sources.Vocabulary.PrimaryLanguage(tag) is { } p && !languages.Contains(p))
                        {
                            languages.Add(p);
                        }
                    }
                }

                skills.Add(new RequestedSkill(id, required, languages));
            }
        }

        var privacy = manifest["privacy"] is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps : "";
        decimal? expected = manifest["expectedCost"]?["perSession"] is JsonValue ev && ev.TryGetValue<string>(out var es) && Money.TryParse(es, out var e) ? e : null;
        return new RequestedManifest(manifest.ToJsonString(), summary, skills, privacy, expected);
    }

    public RequestedSkill? Skill(string id) => Skills.FirstOrDefault(s => s.Id == id);
}

/// <summary>The origins the wallet accepts: https sites, and http on this computer (for tests).</summary>
public static class Origins
{
    public static bool TryParse(string? origin, out string host, out bool secure)
    {
        host = "";
        secure = false;
        if (string.IsNullOrEmpty(origin) || origin.Length > 300) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.GetLeftPart(UriPartial.Authority) != origin) return false;      // exactly scheme://host[:port], no path
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            secure = true;
        }
        else if (uri.Scheme == Uri.UriSchemeHttp && (uri.Host is "localhost" or "127.0.0.1" or "[::1]"))
        {
            secure = false;
        }
        else
        {
            return false;
        }

        host = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
        return true;
    }
}
