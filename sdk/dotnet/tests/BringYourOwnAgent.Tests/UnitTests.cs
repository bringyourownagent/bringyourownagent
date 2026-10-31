using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace BringYourOwnAgent.Tests;

/// <summary>Tests of what is specific to the .NET library: its API, JSON reading and message formatting.</summary>
internal static class UnitTests
{
    private static readonly string M = J(
        "{'byoa':'0.1-draft','purpose':{'summary':'Family geography game: recognise country names',"
        + "'limits':{'maxClipSeconds':15,'maxCallsPerMinute':20}},'skills':{'audio.transcribe':{'languages':['ru','sk'],"
        + "'required':true},'text.understand':{'size':'small','required':false}},'privacy':'personal-voice',"
        + "'expectedCost':{'perSession':'0.06 USD'}}");

    private static readonly string G = J(
        "{'byoa':'0.1-draft','skills':['audio.transcribe','text.understand'],'budget':{'perSession':'0.20 USD'},"
        + "'expiresAt':'2026-10-10T13:00:00Z'}");

    private static readonly string C9 = Call("9");

    private const string PrivacyList = "public, personal, personal-voice, sensitive, local-only";

    public static void Run()
    {
        Check.Run("library version matches the package version", () =>
        {
            Check.Equal("0.0.4", LibraryInfo.Version);
            var informational = typeof(Gate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Check.Equal(LibraryInfo.Version, informational.Split('+')[0], "assembly informational version");
        });

        Check.Run("vocabulary and decisions", () =>
        {
            Check.Equal("allowed,warn,ask,blocked", string.Join(",", Rules.Decisions));
            foreach (Decision decision in Enum.GetValues(typeof(Decision)))
            {
                Check.Equal(Rules.Decisions[(int)decision], decision.ToJsonName(), "decision name");
            }

            Check.Equal(9, Rules.KnownSkills.Count, "skills");
            Check.Equal("local-model,home-server", string.Join(",", Rules.LocalProviders));
            Check.Equal("maxCallsPerMinute,maxClipSeconds,maxFrameBytes,maxTextChars", string.Join(",", Rules.KnownLimits), "limits");
            Check.Equal("model,agent,any", string.Join(",", Rules.SourceKinds), "source kinds");
            Check.Equal("drop,show", string.Join(",", Rules.OnMismatch), "onMismatch");
            Check.Equal("context,input,output,runs", string.Join(",", Rules.NeedsProperties), "needs properties");
            Check.Equal("audio,image,text", string.Join(",", Rules.Modalities), "modalities");
            Check.Equal("api-key,home-server,local", string.Join(",", Rules.Runs), "runs");
            Check.Equal("web.read,web.search", string.Join(",", Rules.Abilities), "abilities");
            Check.Throws<NotSupportedException>(() => ((IList<string>)Rules.KnownSkills).Add("x.y"), "vocabulary is read-only");
        });

        Check.Run("text that is not JSON is data, not an exception", () =>
        {
            Check.Equal(
                J("{'ok':false,'errors':[{'code':'manifest-not-object','path':'','message':'the manifest is not valid JSON'}],'warnings':[]}"),
                Manifest.Validate("{").ToJson());
            foreach (var bad in new[] { "[NaN]", "{\"a\":1,}", "// comment\n{}", "{} x", "Infinity", "", "\"" + (char)0xd83c + "\"" })
            {
                Check.Equal("the manifest is not valid JSON", Manifest.Validate(bad).Errors.Single().Message, "manifest " + bad);
            }

            Check.Equal("the manifest must be a JSON object", Manifest.Validate((string?)null).Errors.Single().Message, "null text");
            Check.Equal("the manifest must be a JSON object", Manifest.Validate(default(JsonElement)).Errors.Single().Message, "undefined element");

            var result = Gate.CheckCall(M, "{not json", "[1,", null);
            Check.Equal("blocked grant-invalid@\"\" the grant is not valid JSON | call-invalid@\"\" the call is not valid JSON", Show(result));

            // The other documents are still checked, and every problem is listed.
            result = Gate.CheckCall("x", "{}", C9, "{");
            Check.Equal(
                "blocked manifest-invalid@\"\" manifest-not-object: the manifest is not valid JSON"
                + " | grant-invalid@\"/byoa\" field \"byoa\" is missing; it names the protocol version, e.g. \"0.1-draft\""
                + " | grant-invalid@\"/skills\" \"skills\" must be an array of skill ids"
                + " | grant-invalid@\"/budget\" \"budget\" must be an object such as {\"perSession\": \"0.20 USD\"}"
                + " | grant-invalid@\"/expiresAt\" \"expiresAt\" must be an RFC 3339 timestamp such as \"2026-10-10T13:00:00Z\""
                + " | state-invalid@\"\" the state is not valid JSON",
                Show(result));
        });

        Check.Run("no state: null, JSON null and an undefined element are the same", () =>
        {
            using var m = JsonDocument.Parse(M);
            using var g = JsonDocument.Parse(G);
            using var c = JsonDocument.Parse(C9);
            using var jsonNull = JsonDocument.Parse("null");
            foreach (var state in new JsonElement?[] { null, default(JsonElement), jsonNull.RootElement })
            {
                Check.Equal("allowed", Show(Gate.CheckCall(m.RootElement, g.RootElement, c.RootElement, state)));
            }

            Check.Equal("allowed", Show(Gate.CheckCall(M, G, C9, null)));
            Check.Equal("allowed", Show(Gate.CheckCall(M, G, C9, "null")));
        });

        Check.Run("duplicate keys: the last one wins, as in JavaScript and Python", () =>
        {
            var manifest = M.Replace(J("{'byoa':'0.1-draft',"), J("{'byoa':'9.9','x':1,'byoa':'0.1-draft','x':2,"));
            var result = Manifest.Validate(manifest);
            Check.True(result.Ok, "valid");
            Check.Equal("field-unknown@/x", string.Join(" ", result.Warnings.Select(w => w.Code + "@" + w.Path)));
        });

        Check.Run("lengths count code points; lone surrogates are kept", () =>
        {
            var summary200 = M.Replace("Family geography game: recognise country names", Esc(0xd83c) + new string('a', 199));
            Check.True(Manifest.Validate(summary200).Ok, "200 code points with a lone surrogate");
            using (var document = JsonDocument.Parse(summary200))
            {
                Check.True(Manifest.Validate(document.RootElement).Ok, "the same as an element");
            }

            var summary201 = M.Replace("Family geography game: recognise country names", Esc(0xd83c) + new string('a', 200));
            Check.Equal("\"purpose.summary\" has 201 characters; the limit is 200", Manifest.Validate(summary201).Errors.Single().Message);

            var loneKey = Open(M) + ",\"" + Esc(0xdc00) + "k\":1}";
            var warning = Manifest.Validate(loneKey).Warnings.Single();
            Check.Equal("/" + (char)0xdc00 + "k", warning.Path, "path");
            Check.Equal("unknown field \"" + Esc(0xdc00) + "k\"; this version ignores it", warning.Message, "message");
        });

        Check.Run("keys are visited in code point order, not UTF-16 order", () =>
        {
            var manifest = Open(M) + ",\"" + Esc(0xd83c) + Esc(0xdf0d) + "\":1,\"" + Esc(0xffff) + "\":2}";
            var paths = Manifest.Validate(manifest).Warnings.Select(w => w.Path).ToList();
            Check.Equal("/" + (char)0xffff + " /" + char.ConvertFromUtf32(0x1F30D), string.Join(" ", paths));
        });

        Check.Run("money stays exact at the largest amounts", () =>
        {
            var grant = G.Replace("0.20 USD", "999999999999.999999 USD");
            var call = C9.Replace("0.003 USD", "999999999999.999999 USD");
            var result = Gate.CheckCall(M, grant, call, J("{'spent':'999999999999.999999 USD'}"));
            Check.Equal(
                "ask budget-exceeded this call (999999999999.999999 USD) would bring the session spend to "
                + "1999999999999.999998 USD, above the budget of 999999999999.999999 USD",
                Show(result));
        });

        Check.Run("numbers in messages are written as JavaScript writes them", () =>
        {
            Check.Equal("the clip is 15.5 seconds; the limit is 15", ClipMessage("15.5", "15"));
            Check.Equal("the clip is 1e-7 seconds; the limit is 0", ClipMessage("1e-7", "0"));
            Check.Equal("the clip is 0.000001 seconds; the limit is 0", ClipMessage("0.000001", "0"));
            Check.Equal("the clip is 100 seconds; the limit is 15", ClipMessage("1E2", "15.0"));
            Check.Equal("the clip is 1 seconds; the limit is 0", ClipMessage("1", "-0"));
            Check.Equal("the clip is 9007199254740991 seconds; the limit is 1.5", ClipMessage("9007199254740991", "1.5"));
            Check.Equal("the clip is 123456789.125 seconds; the limit is 0.1", ClipMessage("123456789.125", "0.1"));
            Check.Equal("the clip is 0.30000000000000004 seconds; the limit is 0.3", ClipMessage("0.30000000000000004", "0.3"));
        });

        Check.Run("messages do not depend on the current culture", () =>
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Check.Equal("the clip is 15.5 seconds; the limit is 15", ClipMessage("15.5", "15"));
                var result = Gate.CheckCall(M, G, C9, J("{'spent':'0.199 USD'}"));
                Check.Equal(
                    "ask budget-exceeded this call (0.003 USD) would bring the session spend to 0.202 USD, above the budget of 0.2 USD",
                    Show(result));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        });

        Check.Run("values in messages are quoted as JSON.stringify quotes them", () =>
        {
            string Version(string value) => Manifest.Validate(M.Replace("\"0.1-draft\"", value)).Errors.Single().Message;
            Check.Equal("protocol version {\"1\":3,\"2\":2,\"b\":1} is not supported; supported: \"0.1-draft\"", Version(J("{'b':1,'2':2,'1':3}")));
            Check.Equal("protocol version 1 is not supported; supported: \"0.1-draft\"", Version("1.0"));
            Check.Equal("protocol version null is not supported; supported: \"0.1-draft\"", Version("1e400"));
            Check.Equal("protocol version true is not supported; supported: \"0.1-draft\"", Version("true"));

            var privacy = "[true,null,\"x\\\"\\n\",{\"k\":[]},-1.5]";
            var message = Manifest.Validate(M.Replace("\"personal-voice\"", privacy)).Errors.Single().Message;
            Check.Equal("privacy class " + privacy + " is unknown; use one of: " + PrivacyList, message);
        });

        Check.Run("very deep JSON is read and quoted without overflowing the stack", () =>
        {
            var deepParams = Open(C9) + ",\"params\":{\"a\":" + new string('[', 900) + new string(']', 900) + "}}";
            Check.Equal("allowed", Show(Gate.CheckCall(M, G, deepParams)));
            var tooDeep = Open(C9) + ",\"params\":{\"a\":" + new string('[', 1100) + new string(']', 1100) + "}}";
            Check.Equal("blocked call-invalid@\"\" the call is not valid JSON", Show(Gate.CheckCall(M, G, tooDeep)));

            var veryDeep = M.Replace("\"personal-voice\"", new string('[', 100000) + new string(']', 100000));
            using var document = JsonDocument.Parse(veryDeep, new JsonDocumentOptions { MaxDepth = 200000 });
            var error = Manifest.Validate(document.RootElement).Errors.Single();
            Check.Equal("privacy-unknown", error.Code);
        });

        Check.Run("opinions are checked when they are made", () =>
        {
            Check.Throws<ArgumentException>(() => new Opinion(Decision.Warn, ""), "empty code");
            Check.Throws<ArgumentOutOfRangeException>(() => new Opinion((Decision)7), "unknown decision");
            Check.Throws<ArgumentOutOfRangeException>(() => ((Decision)7).ToJsonName(), "unknown decision name");
        });

        Check.Run("ApplyOpinion with a typed opinion", () =>
        {
            var allowed = Gate.CheckCall(M, G, C9);
            var blocked = Gate.CheckCall(M, G, Call("42"));
            Check.Equal("allowed", Show(Gate.ApplyOpinion(allowed, (Opinion?)null)));
            Check.Equal("allowed", Show(Gate.ApplyOpinion(allowed, new Opinion(Decision.Allowed, "looks-fine"))));
            Check.Equal(
                J("{'decision':'warn','reasons':[{'code':'purpose-doubt','decision':'warn','message':'the purpose judge doubts that this call fits the declared purpose'}]}"),
                Gate.ApplyOpinion(allowed, new Opinion(Decision.Warn)).ToJson());
            Check.Equal(
                "ask odd the purpose judge doubts that this call fits the declared purpose",
                Show(Gate.ApplyOpinion(allowed, new Opinion(Decision.Ask, "odd", ""))));
            Check.Equal(
                "blocked clip-too-long the clip is 42 seconds; the limit is 15 | odd not a country",
                Show(Gate.ApplyOpinion(blocked, new Opinion(Decision.Warn, "odd", "not a country"))));
            Check.Throws<ArgumentNullException>(() => Gate.ApplyOpinion(null!, (Opinion?)null), "null result");
        });

        Check.Run("ApplyOpinion with an opinion as JSON", () =>
        {
            var allowed = Gate.CheckCall(M, G, C9);
            using var number = JsonDocument.Parse("42");
            Check.Equal(
                "ask purpose-judge-error the purpose judge returned something that is not an opinion; ask the visitor",
                Show(Gate.ApplyOpinion(allowed, number.RootElement)));
            using var jsonNull = JsonDocument.Parse("null");
            Check.Equal("allowed", Show(Gate.ApplyOpinion(allowed, jsonNull.RootElement)));
            Check.Equal("allowed", Show(Gate.ApplyOpinion(allowed, default(JsonElement))));
            using var opinion = JsonDocument.Parse(J("{'decision':'warn','code':'x','message':'y','extra':1}"));
            Check.Equal("warn x y", Show(Gate.ApplyOpinion(allowed, opinion.RootElement)));
        });

        Check.Run("the purpose judge", () =>
        {
            PurposeJudge seeing = (m, g, c) => new Opinion(Decision.Warn, "seen-" + c.GetProperty("skill").GetString());
            Check.Equal("warn seen-audio.transcribe the purpose judge doubts that this call fits the declared purpose", Show(Gate.CheckCall(M, G, C9, null, seeing)));

            PurposeJudge failing = (m, g, c) => throw new TimeoutException("model offline");
            Check.Equal("ask purpose-judge-error the purpose judge failed; ask the visitor", Show(Gate.CheckCall(M, G, C9, null, failing)));

            PurposeJudge malformed = (m, g, c) => new Opinion(Decision.Warn, "");
            Check.Equal("ask purpose-judge-error the purpose judge failed; ask the visitor", Show(Gate.CheckCall(M, G, C9, null, malformed)));

            var consulted = 0;
            PurposeJudge counting = (m, g, c) =>
            {
                consulted++;
                return null;
            };
            Gate.CheckCall(M, G, Call("42"), null, counting);
            Check.Equal(0, consulted, "the judge is not consulted when the call is blocked");
            Gate.CheckCall(M, G, C9, null, counting);
            Check.Equal(1, consulted, "the judge is consulted otherwise");

            JsonElement kept = default;
            Gate.CheckCall(M, G, C9, null, (m, g, c) =>
            {
                kept = c;
                return null;
            });
            Check.Equal("audio.transcribe", kept.GetProperty("skill").GetString(), "elements stay valid after CheckCall(string, ...)");
        });

        Check.Run("results as JSON have the shape of the other libraries", () =>
        {
            Check.Equal(
                J("{'decision':'blocked','reasons':[{'code':'clip-too-long','decision':'blocked','message':'the clip is 42 seconds; the limit is 15'}]}"),
                Gate.CheckCall(M, G, Call("42")).ToJson());
            Check.Equal(
                "{\"decision\":\"blocked\",\"reasons\":[{\"code\":\"grant-invalid\",\"decision\":\"blocked\",\"message\":"
                + "\"\\\"expiresAt\\\" must be an RFC 3339 timestamp such as \\\"2026-10-10T13:00:00Z\\\"\",\"path\":\"/expiresAt\"}]}",
                Gate.CheckCall(M, G.Replace("\"2026-10-10T13:00:00Z\"", "null"), C9).ToJson());
            Check.Equal(
                "{\"ok\":false,\"errors\":[{\"code\":\"privacy-unknown\",\"path\":\"/privacy\",\"message\":"
                + "\"privacy class \\\"secret\\\" is unknown; use one of: " + PrivacyList + "\"}],\"warnings\":[]}",
                Manifest.Validate(M.Replace("personal-voice", "secret")).ToJson());
            var accented = (char)0xe9 + "t" + (char)0xe9;
            var json = Manifest.Validate(Open(M) + ",\"" + accented + "\":1}").ToJson();
            Check.True(json.Contains("\"message\":\"unknown field \\\"" + accented + "\\\"; this version ignores it\""), "non-ASCII stays as it is: " + json);
        });

        Check.Run("results are read-only", () =>
        {
            var result = Gate.CheckCall(M, G, Call("42"));
            Check.Throws<NotSupportedException>(() => ((IList<Reason>)result.Reasons).Add(result.Reasons[0]), "reasons");
            var validation = Manifest.Validate("[]");
            Check.Throws<NotSupportedException>(() => ((IList<Issue>)validation.Errors).Clear(), "errors");
        });

        Check.Run("timestamps follow the RFC 3339 subset of the rules", () =>
        {
            string Expiry(string expiresAt) => Show(Gate.CheckCall(M, G.Replace("2026-10-10T13:00:00Z", expiresAt), C9));
            Check.Equal("allowed", Expiry("2026-10-10t13:00:00z"));
            Check.Equal("allowed", Expiry("2026-10-10T13:00:00-23:59"));
            Check.Equal("blocked grant-invalid@\"/expiresAt\" \"expiresAt\" must be an RFC 3339 timestamp such as \"2026-10-10T13:00:00Z\"", Expiry("2026-10-10T13:00:60Z"));
            Check.Equal("blocked grant-invalid@\"/expiresAt\" \"expiresAt\" must be an RFC 3339 timestamp such as \"2026-10-10T13:00:00Z\"", Expiry("2026-10-10T13:00:00+24:00"));
            Check.Equal("blocked grant-expired the grant expired at 0000-02-29T00:00:00Z", Expiry("0000-02-29T00:00:00Z"));
            Check.Equal("blocked grant-expired the grant expired at 2026-10-10T12:00:00.0009Z", Expiry("2026-10-10T12:00:00.0009Z"));
            Check.Equal("allowed", Expiry("2026-10-10T12:00:00.001Z"));
        });

        Check.Run("the example from the README", () =>
        {
            var examples = Path.Combine(Path.GetDirectoryName(VectorTests.FindDirectory())!, "examples");
            var manifest = File.ReadAllText(Path.Combine(examples, "manifest.json"));
            Check.True(Manifest.Validate(manifest).Ok, "the example manifest is valid");
            var grant = J("{'byoa':'0.1-draft','skills':['audio.transcribe'],'budget':{'perSession':'0.20 USD'},'expiresAt':'2026-10-10T13:00:00Z'}");
            var call = J("{'skill':'audio.transcribe','at':'2026-10-10T12:00:00Z','provider':'local-model','estimatedCost':'0 USD','input':{'clipSeconds':42}}");
            var result = Gate.CheckCall(manifest, grant, call, J("{'spent':'0.04 USD'}"));
            Check.Equal(Decision.Blocked, result.Decision, "decision");
            Check.Equal("clip-too-long", result.Reasons[0].Code, "first reason");
        });
    }

    /// <summary>JSON written with single quotes, for readability.</summary>
    private static string J(string text) => text.Replace('\'', '"');

    /// <summary>A JSON object without its closing brace, to add fields to.</summary>
    private static string Open(string json) => json.Substring(0, json.Length - 1);

    /// <summary>A JSON escape for one UTF-16 unit (kept out of the source text on purpose).</summary>
    private static string Esc(int unit) => "\\" + "u" + unit.ToString("x4", CultureInfo.InvariantCulture);

    private static string Call(string clipSeconds) =>
        J("{'skill':'audio.transcribe','at':'2026-10-10T12:00:00Z','provider':'api-key','estimatedCost':'0.003 USD','input':{'clipSeconds':" + clipSeconds + "}}");

    private static string ClipMessage(string clipSeconds, string limit)
    {
        var manifest = M.Replace("\"maxClipSeconds\":15", "\"maxClipSeconds\":" + limit);
        var result = Gate.CheckCall(manifest, G, Call(clipSeconds));
        return result.Reasons.SingleOrDefault(r => r.Code == "clip-too-long")?.Message ?? Show(result);
    }

    /// <summary>"decision code@"path" message | ..." for short, exact comparisons.</summary>
    private static string Show(CheckResult result) =>
        string.Join(
            " | ",
            new[] { result.Decision.ToJsonName() + (result.Reasons.Count == 0 ? "" : " " + Reason(result.Reasons[0])) }
                .Concat(result.Reasons.Skip(1).Select(Reason)));

    private static string Reason(Reason reason) =>
        reason.Code + (reason.Path is null ? "" : "@\"" + reason.Path + "\"") + " " + reason.Message;
}
