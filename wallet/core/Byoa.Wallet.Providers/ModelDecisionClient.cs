using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Byoa.Wallet.Guard;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// One of the visitor's own text sources acting as the guard's judge: a model on this computer (the Llama app, LM Studio,
/// Ollama), or a paid one behind his key. Unlike Jev it writes text, so it gets the guard's statements in one prompt and
/// must answer strict JSON with a chance per statement; anything else — reasoning around the JSON, code fences, words —
/// is a failure the guard treats as "unavailable", never a pass, and none of the model's own text leaks anywhere.
/// An input longer than <c>maxInputChars</c> is judged in three chunks (start, middle, end) combined conservatively.
/// A local model costs nothing; a paid source's own cost comes back and is counted like Jev's.
/// </summary>
public sealed class ModelDecisionClient(ITextSource source, int maxInputChars) : IDecisionClient
{
    /// <summary>The longest the visitor's site waits for the guard (docs: no answer in two minutes is a refusal).</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Room for the JSON answer and for a model that reasons before it: the parse never sees anything but the JSON.</summary>
    const int MaxAnswerTokens = 400;

    /// <summary>A chunk is never smaller than this, so a mistyped setting cannot cut the request into slivers.</summary>
    const int MinInputChars = 500;

    static readonly string SystemPrompt = "You are the guard of an AI agent gateway. You judge statements about a website's request and " +
        "answer with one JSON object only: no reasoning, no explanations, just the JSON object.";

    public bool HasKey => true;                          // a text source needs no key; a missing key is Jev's affair

    public async Task<DecisionAnswer> AskAsync(JsonObject state, IReadOnlyDictionary<string, string> statements, CancellationToken ct)
    {
        var (field, text) = LongField(state);
        if (field is null || text.Length <= MaxInputChars)
            return await AskOnce(state, statements, ct).ConfigureAwait(false);

        // Beyond one window the model sees only pieces: judge start, middle and end (orders hide at the ends), and take the
        // worse side of each statement, so no piece can quietly let the request through.
        var chars = MaxInputChars;
        var middle = (text.Length - chars) / 2;
        var pieces = new[] { text[..chars], text.Substring(middle, chars), text[^chars..] }.Distinct().ToList();
        var asked = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var piece in pieces)
        {
            var one = (JsonObject)state.DeepClone();
            var parts = field.Split('.');
            ((JsonObject)one[parts[0]]!)[parts[1]] = piece;
            asked[piece] = await AskOnce(one, statements, ct).ConfigureAwait(false);
        }

        var cost = asked.Values.Sum(a => a.Cost);
        var combined = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in statements.Keys)
        {
            var chances = pieces.Select(piece => asked[piece].Yes[name]);
            combined[name] = name == GuardQuestions.OnPurpose ? chances.Min() : chances.Max();
        }

        return new DecisionAnswer(combined, cost);
    }

    /// <summary>
    /// The field the input is judged by, when it alone is longer than one window: the request's text, else a site's goal.
    /// The name is the path in the state, so a chunk can be put back in its place.
    /// </summary>
    static (string? Field, string Text) LongField(JsonObject state)
    {
        var request = (state["request"]?["text"] as JsonValue)?.GetValue<string>();
        if (request is not null) return ("request.text", request);
        var purpose = (state["site"]?["declared_purpose"] as JsonValue)?.GetValue<string>();
        return (purpose is null ? null : "site.declared_purpose", purpose ?? "");
    }

    int MaxInputChars => Math.Max(MinInputChars, maxInputChars);

    async Task<DecisionAnswer> AskOnce(JsonObject state, IReadOnlyDictionary<string, string> statements, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        TextAnswer answer;
        try
        {
            answer = await source.GenerateAsync(new TextRequest(
            [
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", Prompt(state, statements)),
            ], MaxAnswerTokens, 0, null, null, Thinking: false), limit.Token).ConfigureAwait(false);
        }
        catch (TextSourceException e)
        {
            throw new DecisionException(string.IsNullOrWhiteSpace(e.Message) ? "the model chosen to check requests could not be asked" : e.Message, e.Cost ?? 0, e);
        }
        catch (InvalidOperationException e)
        {
            throw new DecisionException("the model chosen to check requests could not be asked: " + e.Message, 0, e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DecisionException("the model chosen to check requests did not answer in time");
        }

        return new DecisionAnswer(Parse(answer.Text, statements.Keys), answer.Cost);
    }

    /// <summary>The prompt, the same shape for every source: the call as JSON, then the statements, then the JSON-only answer.</summary>
    internal static string Prompt(JsonObject state, IReadOnlyDictionary<string, string> statements)
    {
        var purpose = state["request"] is null;
        var lines = new List<string>
        {
            purpose
                ? "Judge the declared purpose of a website that wants the visitor's own AI agent."
                : "Judge the following about a website that asks the visitor's own AI agent to answer.",
            "",
            "The facts of the call, as JSON:",
            "<<<STATE",
            state.ToJsonString(),
            "STATE>>>",
            "",
            "For each statement below, give the chance (a number from 0 to 1) that it is TRUE of " +
                (purpose ? "`site.declared_purpose`" : "`request.text`") + ".",
        };
        foreach (var (name, text) in statements) lines.Add("- " + name + ": " + text);
        lines.Add("");
        lines.Add("Answer with one strict JSON object and nothing else: {" +
                  string.Join(", ", statements.Keys.Select(n => "\"" + n + "\": <0..1>")) + "}");
        return string.Join("\n", lines);
    }

    static readonly Regex Reasoning = new("<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.Compiled);

    static readonly Regex Fence = new("```[a-zA-Z0-9]*\\s*(.*?)\\s*```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Reads the chances out of whatever the model wrote: past its reasoning, past code fences, out of the first balanced
    /// braces, numbers bare or in quotes. Nothing usable in an answer is a <see cref="DecisionException"/>: the guard then
    /// treats the judge as unavailable, never as a pass.
    /// </summary>
    internal static IReadOnlyDictionary<string, double> Parse(string? text, IEnumerable<string> names)
    {
        var body = text is null ? "" : Reasoning.Replace(text, " ").Replace("</think>", " ");
        if (Fence.Match(body) is { Success: true } fence) body = fence.Groups[1].Value;
        JsonNode? root = null;
        if (FindObject(body) is { } json)
        {
            try { root = JsonNode.Parse(json); }
            catch (System.Text.Json.JsonException) { /* falls through to the failure below */ }
        }

        var yes = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (root?[name] is not JsonValue value || !TryChance(value, out var chance))
                throw new DecisionException("the guard did not answer \"" + name + "\" in a form the wallet can use");
            yes[name] = chance;
        }

        return yes;
    }

    /// <summary>The first balanced {...} in the text, or null.</summary>
    static string? FindObject(string body)
    {
        var start = body.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        for (var i = start; i < body.Length; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}' && --depth == 0) return body[start..(i + 1)];
        }

        return null;
    }

    /// <summary>A chance: a bare number or one in quotes, 0 to 1, never anything else.</summary>
    static bool TryChance(JsonValue value, out double chance)
    {
        if (value.TryGetValue<double>(out chance) && !double.IsNaN(chance) && chance is >= 0 and <= 1) return true;
        if (value.TryGetValue<string>(out var text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out chance)
            && chance is >= 0 and <= 1) return true;
        chance = 0;
        return false;
    }
}
