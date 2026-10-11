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

/// <summary>
/// Simple routing rules (0.1 has no AI manager): walk the skill's chain in the visitor's order and keep every
/// source that is switched on, can run, and knows the call's language. The "manager suggests … because …" text
/// is made from the same properties.
/// </summary>
public static class Router
{
    /// <summary>The default chain for audio.transcribe: GigaAM (Russian), Whisper (other languages and the fallback), OpenRouter last (paid, asks first).</summary>
    public static readonly string[] DefaultTranscribeChain = ["gigaam-v3", "whisper", "openrouter-scribe"];

    /// <summary>Adds the default chain and the default stops to settings that miss them.</summary>
    public static void EnsureDefaults(WalletSettings settings)
    {
        var chain = settings.Chain(Vocabulary.Transcribe);
        foreach (var id in DefaultTranscribeChain)
        {
            if (chain.Stops.All(s => s.SourceId != id)) chain.Stops.Add(new ChainStop { SourceId = id });
        }

        foreach (var id in DefaultTranscribeChain) settings.Source(id);
    }

    /// <summary>A source knows the language when it lists it, or when it is multilingual. A specialised source needs a known language.</summary>
    public static bool Knows(ISpeechSource source, string? language) =>
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

            if (why is null) steps.Add(new RouteStep(source, source.Paid && stop.AskBeforePaid));
            else skipped.Add(new SkippedSource(source, why));
        }

        return new RoutePlan(steps, skipped);
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
}
