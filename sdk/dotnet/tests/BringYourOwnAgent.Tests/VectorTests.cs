using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace BringYourOwnAgent.Tests;

/// <summary>
/// The shared test vectors in sdk/test-vectors: the contract with the Python and Node.js libraries.
/// See sdk/test-vectors/README.md for the format.
/// </summary>
internal static class VectorTests
{
    private static readonly JsonDocumentOptions DeepOptions = new() { MaxDepth = 1000 };

    /// <summary>Runs every case; adds each full result (as JSON) to <paramref name="dump"/>. Returns the number of cases.</summary>
    public static int Run(string directory, List<string> dump)
    {
        var count = 0;

        using (var file = Load(directory, "validate-manifest.json"))
        {
            var defaults = file.RootElement.GetProperty("defaults");
            foreach (var testCase in file.RootElement.GetProperty("cases").EnumerateArray())
            {
                count++;
                var name = "validate-manifest.json: " + testCase.GetProperty("name").GetString();
                Check.Run(name, () =>
                {
                    var text = CaseDocument(testCase, defaults, "manifest");
                    using var manifest = JsonDocument.Parse(text, DeepOptions);
                    var result = Manifest.Validate(manifest.RootElement);
                    dump.Add(result.ToJson());
                    Check.Equal(Expected(testCase.GetProperty("expect"), validation: true), Project(result), "result");
                    Check.Equal(result.ToJson(), Manifest.Validate(text).ToJson(), "the JSON text overload");
                    foreach (var issue in result.Errors.Concat(result.Warnings))
                    {
                        Check.True(issue.Message.Length > 0, "every issue has a message");
                    }
                });
            }
        }

        foreach (var fileName in new[] { "check-call.json", "purpose-judge.json" })
        {
            using var file = Load(directory, fileName);
            var defaults = file.RootElement.GetProperty("defaults");
            foreach (var testCase in file.RootElement.GetProperty("cases").EnumerateArray())
            {
                count++;
                var name = fileName + ": " + testCase.GetProperty("name").GetString();
                Check.Run(name, () =>
                {
                    var texts = new[] { "manifest", "grant", "call", "state" }.Select(n => CaseDocument(testCase, defaults, n)).ToArray();
                    var documents = texts.Select(t => JsonDocument.Parse(t, DeepOptions)).ToArray();
                    try
                    {
                        var (m, g, c, s) = (documents[0].RootElement, documents[1].RootElement, documents[2].RootElement, documents[3].RootElement);
                        testCase.TryGetProperty("judge", out var judgeSpec);
                        var result = CheckWithJudge(judgeSpec, m, g, c, s, out var viaText);
                        dump.Add(result.ToJson());
                        Check.Equal(Expected(testCase.GetProperty("expect"), validation: false), Project(result), "result");
                        Check.Equal(result.ToJson(), viaText(texts[0], texts[1], texts[2], texts[3]).ToJson(), "the JSON text overload");
                        foreach (var reason in result.Reasons)
                        {
                            Check.True(reason.Message.Length > 0, "every reason has a message");
                            Check.True(Rules.Decisions.Contains(reason.Decision.ToJsonName()), "every reason has a known decision");
                        }
                    }
                    finally
                    {
                        foreach (var document in documents)
                        {
                            document.Dispose();
                        }
                    }
                });
            }
        }

        return count;
    }

    /// <summary>The sdk/test-vectors directory, found by walking up from the program.</summary>
    public static string FindDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("BYOA_TEST_VECTORS");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment!;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "test-vectors");
            if (File.Exists(Path.Combine(candidate, "check-call.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("shared test vectors not found; run the tests from the repository or set BYOA_TEST_VECTORS");
    }

    private delegate CheckResult TextCheck(string manifest, string grant, string call, string state);

    /// <summary>
    /// Runs the gate with the judge a purpose-judge.json case asks for. A typed C# judge can only
    /// return an <see cref="Opinion"/> or null, so a "returns" value that is not a well-formed opinion
    /// goes through <see cref="Gate.ApplyOpinion(CheckResult, JsonElement)"/>, the path by which such
    /// JSON reaches a .NET wallet (a model's answer). A "notCallable" value cannot be passed at all:
    /// the compiler rejects anything but a function, so it runs as a judge that cannot be invoked (throws).
    /// </summary>
    private static CheckResult CheckWithJudge(
        JsonElement spec, JsonElement m, JsonElement g, JsonElement c, JsonElement s, out TextCheck viaText)
    {
        if (spec.ValueKind == JsonValueKind.Undefined || spec.ValueKind == JsonValueKind.Null)
        {
            viaText = (mt, gt, ct, st) => Gate.CheckCall(mt, gt, ct, st);
            return Gate.CheckCall(m, g, c, s);
        }

        PurposeJudge judge;
        if (spec.TryGetProperty("returns", out var returns))
        {
            var returnsCopy = returns.Clone();
            if (TypedOpinion(returns, out var opinion))
            {
                judge = (_, _, _) => opinion;
                var typed = Gate.CheckCall(m, g, c, s, judge);
                var viaJson = Gate.CheckCall(m, g, c, s);
                if (viaJson.Decision != Decision.Blocked)
                {
                    viaJson = Gate.ApplyOpinion(viaJson, returnsCopy);
                }

                Check.Equal(typed.ToJson(), viaJson.ToJson(), "the same opinion given as JSON");
            }
            else
            {
                viaText = (mt, gt, ct, st) => ApplyJson(Gate.CheckCall(mt, gt, ct, st), returnsCopy);
                return ApplyJson(Gate.CheckCall(m, g, c, s), returnsCopy);
            }
        }
        else if (spec.TryGetProperty("throws", out var throws))
        {
            var message = throws.GetString();
            judge = (_, _, _) => throw new InvalidOperationException(message);
        }
        else if (spec.TryGetProperty("notCallable", out _))
        {
            judge = (_, _, _) => throw new InvalidOperationException("this judge cannot be invoked");
        }
        else
        {
            throw new InvalidOperationException("unknown judge spec " + spec.GetRawText());
        }

        viaText = (mt, gt, ct, st) => Gate.CheckCall(mt, gt, ct, st, judge);
        return Gate.CheckCall(m, g, c, s, judge);
    }

    private static CheckResult ApplyJson(CheckResult result, JsonElement opinion) =>
        result.Decision == Decision.Blocked ? result : Gate.ApplyOpinion(result, opinion);

    /// <summary>A JSON value a typed judge can return: null, or a well-formed opinion.</summary>
    private static bool TypedOpinion(JsonElement value, out Opinion? opinion)
    {
        opinion = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("decision", out var decision)
            || decision.ValueKind != JsonValueKind.String
            || !Rules.Decisions.Contains(decision.GetString()))
        {
            return false;
        }

        string? code = null;
        if (value.TryGetProperty("code", out var codeValue) && codeValue.ValueKind != JsonValueKind.Null)
        {
            if (codeValue.ValueKind != JsonValueKind.String || codeValue.GetString()!.Length == 0)
            {
                return false;
            }

            code = codeValue.GetString();
        }

        string? message = null;
        if (value.TryGetProperty("message", out var messageValue) && messageValue.ValueKind != JsonValueKind.Null)
        {
            if (messageValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            message = messageValue.GetString();
        }

        var parsed = (Decision)Rules.Decisions.ToList().IndexOf(decision.GetString()!);
        opinion = new Opinion(parsed, code, message);
        return true;
    }

    private static string Project(ValidationResult result) =>
        "ok=" + (result.Ok ? "true" : "false")
        + " errors=[" + string.Join(", ", result.Errors.Select(i => i.Code + "@\"" + i.Path + "\"")) + "]"
        + " warnings=[" + string.Join(", ", result.Warnings.Select(i => i.Code + "@\"" + i.Path + "\"")) + "]";

    private static string Project(CheckResult result) =>
        result.Decision.ToJsonName() + " reasons=["
        + string.Join(", ", result.Reasons.Select(r => r.Path is null ? r.Code : r.Code + "@\"" + r.Path + "\"")) + "]";

    private static string Expected(JsonElement expect, bool validation)
    {
        string Items(JsonElement list) => string.Join(", ", list.EnumerateArray().Select(item =>
            item.TryGetProperty("path", out var path)
                ? item.GetProperty("code").GetString() + "@\"" + path.GetString() + "\""
                : item.GetProperty("code").GetString()));

        if (validation)
        {
            return "ok=" + (expect.GetProperty("ok").GetBoolean() ? "true" : "false")
                + " errors=[" + Items(expect.GetProperty("errors")) + "]"
                + " warnings=[" + Items(expect.GetProperty("warnings")) + "]";
        }

        return expect.GetProperty("decision").GetString() + " reasons=[" + Items(expect.GetProperty("reasons")) + "]";
    }

    private static JsonDocument Load(string directory, string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name), Encoding.UTF8), DeepOptions);

    /// <summary>
    /// The JSON text of one input document of a case: the case's own value, or the default with
    /// the case's merge patch (RFC 7396) applied, or the default. Numbers keep their exact text.
    /// </summary>
    private static string CaseDocument(JsonElement testCase, JsonElement defaults, string name)
    {
        if (testCase.TryGetProperty(name, out var own))
        {
            return own.GetRawText();
        }

        var text = new StringBuilder();
        if (testCase.TryGetProperty(name + "Patch", out var patch))
        {
            WriteMerged(text, defaults.GetProperty(name), patch);
        }
        else
        {
            text.Append(defaults.GetProperty(name).GetRawText());
        }

        return text.ToString();
    }

    private static void WriteMerged(StringBuilder text, JsonElement target, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            text.Append(patch.GetRawText());
            return;
        }

        // Existing keys keep their place; new keys are added at the end, as in Python and JavaScript.
        var names = new List<string>();
        var targets = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var patches = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (target.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in target.EnumerateObject())
            {
                names.Add(property.Name);
                targets[property.Name] = property.Value;
            }
        }

        foreach (var property in patch.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                names.Remove(property.Name);
                continue;
            }

            if (!names.Contains(property.Name))
            {
                names.Add(property.Name);
            }

            patches[property.Name] = property.Value;
        }

        text.Append('{');
        for (var i = 0; i < names.Count; i++)
        {
            var key = names[i];
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(JsonSerializer.Serialize(key)).Append(':');
            if (patches.TryGetValue(key, out var patchValue))
            {
                WriteMerged(text, targets.TryGetValue(key, out var targetValue) ? targetValue : default, patchValue);
            }
            else
            {
                text.Append(targets[key].GetRawText());
            }
        }

        text.Append('}');
    }
}
