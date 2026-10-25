using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Sources;

/// <summary>
/// What the Get started card offers while the wallet has no text model yet: the models a running program on this
/// computer already serves (an Add per model, the same draft the Add a model window writes), and the free demo
/// presets that answer with no sign-up at all. Pure decisions, no UI — the settings window only draws them.
/// </summary>
public sealed record LocalModelOffer(
    string Kind,                // the source kind to add ("lmstudio", "ollama", "openai-compatible")
    string Endpoint,            // the address the draft carries (Ollama's gets /v1, exactly like Add a model)
    string RuntimeName,         // the name a person knows the program by ("LM Studio", "Ollama", "Llama")
    string Model,               // the runtime's own model id
    string? ModelName,          // the name people see, when the runtime says one
    long? SizeBytes,
    bool Loaded,                // in memory right now (only meaningful when LoadedKnown)
    bool LoadedKnown,
    string SourceId);           // the id the source will have once added

public static class GetStarted
{
    /// <summary>
    /// The models the running programs on this computer serve: the ones already added are left out, the ones in
    /// memory come first. A program that does not answer right now is not offered — nothing here starts a program.
    /// </summary>
    public static IReadOnlyList<LocalModelOffer> Local(IReadOnlyList<RuntimeInfo> runtimes, IReadOnlyList<SourceSetting> sources)
    {
        var taken = sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var offers = new List<LocalModelOffer>();
        foreach (var runtime in runtimes.Where(r => r.Running))
        {
            // The same endpoint Add a model writes for the kind (Ollama's OpenAI-compatible address ends in /v1).
            var endpoint = runtime.Kind.Trim().ToLowerInvariant() == SourceKinds.Ollama
                ? runtime.Endpoint.Trim().TrimEnd('/') + "/v1"
                : runtime.Endpoint;
            foreach (var model in runtime.Models
                         .OrderByDescending(m => m.LoadedKnown && m.Loaded)
                         .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
            {
                var offer = new LocalModelOffer(runtime.Kind, endpoint, LocalRuntimes.Name(runtime.Kind, runtime.Endpoint),
                    model.Id, model.Name, model.SizeBytes, model.LoadedKnown && model.Loaded, model.LoadedKnown,
                    SourceIds.OpenAiCompatible(runtime.Kind, model.Id, endpoint));
                if (!taken.Contains(offer.SourceId)) offers.Add(offer);
            }
        }

        return offers;
    }

    /// <summary>Has the wallet a text model at all: any added source that can answer text.generate, on or off.</summary>
    public static bool HasTextModel(WalletSettings settings) =>
        settings.Sources.Any(s => s.Kind is not null && s.Skills.Contains(Vocabulary.Generate));

    /// <summary>The free demo presets of the card, in the order they are offered.</summary>
    public static IReadOnlyList<ApiService> DemoPresets => ApiServices.Demo;

    /// <summary>The model a demo preset answers as, with no key: llm7.io routes "default" itself; OVHcloud's anonymous endpoint serves Nemo.</summary>
    public static string DemoModel(string service) =>
        service.Trim().ToLowerInvariant() == "ovh" ? "Mistral-Nemo-Instruct-2407" : "default";

    /// <summary>The one plain confirmation before a demo preset is added: it names the risks of a public service.</summary>
    public const string DemoWarning =
        "A free demo model answers on a public service, with no key and no sign-up: that service may keep what you send, it is slow and limited, "
        + "and it is not for private text. Nothing you allow for sites changes until you switch it on for a site. Add it?";
}
