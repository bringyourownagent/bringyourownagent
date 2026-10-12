using System.Globalization;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Sources;

/// <summary>One stop of a route: a source to try, and whether to ask the visitor first (a paid source).</summary>
public sealed record RouteStep(ISpeechSource Source, bool AskFirst);

/// <summary>A source left out of a route, and why (for the "why?" text and the activity log).</summary>
public sealed record SkippedSource(ISpeechSource Source, string Why);

/// <summary>The sources to try for one call, in order.</summary>
public sealed record RoutePlan(IReadOnlyList<RouteStep> Steps, IReadOnlyList<SkippedSource> Skipped)
{
    public RouteStep? First => Steps.Count > 0 ? Steps[0] : null;
}

/// <summary>One stop of a text route: the same as <see cref="RouteStep"/>, for <see cref="ITextSource"/>s.</summary>
public sealed record TextRouteStep(ITextSource Source, bool AskFirst);

/// <summary>A text source left out of a route, and why.</summary>
public sealed record TextSkippedSource(ITextSource Source, string Why);

/// <summary>The text sources to try for one call, in order.</summary>
public sealed record TextRoutePlan(IReadOnlyList<TextRouteStep> Steps, IReadOnlyList<TextSkippedSource> Skipped)
{
    public TextRouteStep? First => Steps.Count > 0 ? Steps[0] : null;
}

/// <summary>Whether a source's runtime or API answered just now: null not checked yet, false it did not.</summary>
public interface ILiveness
{
    bool? Up { get; }
}

/// <summary>
/// Simple routing rules (0.1 has no AI manager): walk the skill's chain in the visitor's order and keep every
/// source that is switched on, can run, and knows the call's language. The "manager suggests … because …" text
/// is made from the same properties.
/// </summary>
public static class Router
{
    /// <summary>The default chain for audio.transcribe: GigaAM (Russian), Whisper (other languages and the fallback), a
    /// small Whisper on the CPU for machines without a usable GPU Whisper, OpenRouter last (paid, asks first).</summary>
    public static readonly string[] DefaultTranscribeChain = ["gigaam-v3", WhisperId, CpuWhisperId, "openrouter-scribe"];

    /// <summary>The sources of the built-in speech chain, by their ids in the settings.</summary>
    public const string GigaAmId = "gigaam-v3";
    public const string WhisperId = "whisper";
    public const string CpuWhisperId = "whisper-cpu";

    /// <summary>Adds the default chains and the default stops to settings that miss them.</summary>
    public static void EnsureDefaults(WalletSettings settings)
    {
        var chain = settings.Chain(Vocabulary.Transcribe);
        foreach (var id in DefaultTranscribeChain)
        {
            if (chain.Stops.All(s => s.SourceId != id)) chain.Stops.Add(new ChainStop { SourceId = id });
        }

        foreach (var id in DefaultTranscribeChain) settings.Source(id);
    }

    /// <summary>
    /// Adds a stop for every text source that does not have one yet, before the first paid stop (local models answer
    /// first). Sources a visitor (or his agent) adds are appended to the text chain here, once, when they appear.
    /// </summary>
    public static void EnsureTextChain(WalletSettings settings, IEnumerable<ITextSource> sources)
    {
        var chain = settings.Chain(Vocabulary.Generate);
        foreach (var source in sources)
        {
            if (chain.Stops.Any(s => s.SourceId == source.Id)) continue;
            var paid = sources.FirstOrDefault(x => chain.Stops.Any(s => s.SourceId == x.Id) && x.Paid);
            var index = paid is null ? chain.Stops.Count : chain.Stops.FindIndex(s => s.SourceId == paid.Id);
            chain.Stops.Insert(index, new ChainStop { SourceId = source.Id });
        }
    }

    /// <summary>A source knows the language when it lists it, or when it is multilingual. A specialised source needs a known language.</summary>
    public static bool Knows(ISpeechSource source, string? language) =>
        source.Languages is null || (language is not null && source.Languages.Contains(language));

    /// <summary>The same language rule for a text source.</summary>
    public static bool KnowsText(ITextSource source, string? language) =>
        source.Languages is null || (language is not null && source.Languages.Contains(language));

    public static RoutePlan Plan(SkillChain chain, IEnumerable<ISpeechSource> sources, Func<string, bool> enabled, string? language)
    {
        var byId = sources.ToDictionary(s => s.Id);
        var steps = new List<RouteStep>();
        var skipped = new List<SkippedSource>();
        foreach (var stop in chain.Stops)
        {
            if (!byId.TryGetValue(stop.SourceId, out var source)) continue;
            string? why = null;
            if (!stop.Enabled || !enabled(source.Id)) why = "turned off";
            else if (!Knows(source, language)) why = language is null ? "needs to know the language" : "does not know " + Vocabulary.LanguageName(language);
            else if (source.State is SourceState.Missing) why = "model not found";
            else if (source.State is SourceState.NoKey) why = "no API key";
            else if (source.State is SourceState.Broken) why = "failed to load";
            else if (stop.SourceId == CpuWhisperId && byId.TryGetValue(WhisperId, out var gpu) && gpu.State is SourceState.Ready or SourceState.Downloading)
            {
                why = "the big Whisper already works on this PC";
            }

            if (why is null) steps.Add(new RouteStep(source, source.Paid && stop.AskBeforePaid));
            else skipped.Add(new SkippedSource(source, why));
        }

        return new RoutePlan(steps, skipped);
    }

    /// <summary>
    /// The same walk for text.generate: the chain's stops in the visitor's order, only sources that are switched on,
    /// can run (their runtime is up, their key is added) and know the language.
    /// </summary>
    public static TextRoutePlan PlanText(SkillChain chain, IEnumerable<ITextSource> sources, Func<string, bool> enabled, string? language = null)
    {
        var byId = sources.ToDictionary(s => s.Id);
        var steps = new List<TextRouteStep>();
        var skipped = new List<TextSkippedSource>();
        foreach (var stop in chain.Stops)
        {
            if (!byId.TryGetValue(stop.SourceId, out var source)) continue;
            string? why = null;
            if (!stop.Enabled || !enabled(source.Id)) why = "turned off";
            else if (!KnowsText(source, language)) why = language is null ? "needs to know the language" : "does not know " + Vocabulary.LanguageName(language);
            else if (source is ILiveness { Up: false }) why = "not running";
            else if (source.State is SourceState.Missing) why = "model not found";
            else if (source.State is SourceState.NoKey) why = "no API key";
            else if (source.State is SourceState.Broken) why = "failed to load";

            if (why is null) steps.Add(new TextRouteStep(source, source.Paid && stop.AskBeforePaid));
            else skipped.Add(new TextSkippedSource(source, why));
        }

        return new TextRoutePlan(steps, skipped);
    }

    /// <summary>"GigaAM v3 for Russian, because it is made for Russian, runs on this PC with no cloud bill, about 0.2 s a clip."</summary>
    public static string Explain(RoutePlan plan, string? language)
    {
        var first = plan.First;
        var lang = language is null ? "" : " for " + Vocabulary.LanguageName(language);
        if (first is null)
        {
            return "None of your sources can do this" + lang + ". Add a model or a key in Detailed settings.";
        }

        var s = first.Source;
        var because = new List<string>();
        if (s.Languages is not null && language is not null && s.Languages.Count <= 3)
        {
            because.Add("it is made for " + string.Join(" and ", s.Languages.Select(Vocabulary.LanguageName)));
        }
        else
        {
            var specialised = plan.Skipped.FirstOrDefault(k => k.Source.Languages is not null && !Knows(k.Source, language));
            if (specialised is not null)
            {
                because.Add(specialised.Source.Name + " knows only " + string.Join(", ", specialised.Source.Languages!.Select(Vocabulary.LanguageName)));
            }

            because.Add("it knows " + s.LanguageCount.ToString(CultureInfo.InvariantCulture) + " languages");
        }

        because.Add(s.Paid
            ? "it is paid, about " + Money.Show(s.EstimateCost(5)) + " for a 5 s clip" + (first.AskFirst ? ", and it asks you first" : "")
            : "it runs on this PC with no cloud bill");
        because.Add("about " + s.ApproxSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s a clip");

        var text = "Manager suggests " + s.Name + lang + " because " + JoinAnd(because) + ".";
        var rest = plan.Steps.Skip(1).Select(x => x.Source.Name + (x.Source.Paid ? x.AskFirst ? " (paid, asks first)" : " (paid)" : "")).ToList();
        if (rest.Count > 0) text += " If it cannot answer: " + string.Join(", then ", rest) + ".";
        return text;
    }

    static string JoinAnd(List<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    /// <summary>"Manager suggests qwen3.5-4b in LM Studio because it runs on this PC with no cloud bill."</summary>
    public static string ExplainText(TextRoutePlan plan)
    {
        var first = plan.First;
        if (first is null)
        {
            return "None of your sources can do this. Add a model in LM Studio or Ollama, or an API key, in Detailed settings.";
        }

        var s = first.Source;
        var because = s.Paid
            ? "it is paid, about " + Money.Show(s.EstimateCost(TypicalRequest)) + " for a short answer" + (first.AskFirst ? ", and it asks you first" : "")
            : "it runs on this PC with no cloud bill";
        var text = "Manager suggests " + s.Name + " because " + because + ".";
        var rest = plan.Steps.Skip(1).Select(x => x.Source.Name + (x.Source.Paid ? x.AskFirst ? " (paid, asks first)" : " (paid)" : "")).ToList();
        if (rest.Count > 0) text += " If it cannot answer: " + string.Join(", then ", rest) + ".";
        return text;
    }

    /// <summary>A small make-believe call, only for showing an estimate for one.</summary>
    static TextRequest TypicalRequest { get; } =
        new([new ChatMessage("user", "Hello!")], 512, 0.7, null, null);
}
