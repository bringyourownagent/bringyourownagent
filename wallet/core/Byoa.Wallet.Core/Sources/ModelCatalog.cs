namespace Byoa.Wallet.Sources;

/// <summary>One model of the public catalog (OpenRouter's list, which needs no key).</summary>
/// <param name="Id">The catalog's own id ("anthropic/claude-opus-4.6", "meta-llama/llama-4-scout:free").</param>
/// <param name="Name">Its name for people ("Google: Gemma 3 12B").</param>
/// <param name="PromptPerToken">Dollars per input token, or null when the list says nothing.</param>
/// <param name="CompletionPerToken">Dollars per output token, or null.</param>
/// <param name="InputModalities">What goes into the model (<c>architecture.input_modalities</c>); null when the list says nothing.</param>
/// <param name="OutputModalities">What comes out (<c>architecture.output_modalities</c>); null when the list says nothing. A model that draws
/// pictures is kept by the catalog and reaches no text picker (see <see cref="OffersText"/>).</param>
public sealed record CatalogModel(string Id, string Name, int ContextLength, decimal? PromptPerToken, decimal? CompletionPerToken, long Created,
    IReadOnlyList<string>? InputModalities = null, IReadOnlyList<string>? OutputModalities = null)
{
    /// <summary>The catalog's price is zero both ways (a ":free" variant or an open model somebody pays for).</summary>
    public bool Free => PromptPerToken is 0 && CompletionPerToken is 0;

    /// <summary>About what a short debate answer costs: 1,500 tokens in, 700 out. Null without prices.</summary>
    public decimal? TypicalAnswerCost => PromptPerToken is { } p && CompletionPerToken is { } c ? p * 1500 + c * 700 : null;

    /// <summary>The primary-role rule for a text picker (PROPOSAL-v2 3.3): a model the catalog says nothing about is offered, one that writes
    /// text and does not draw pictures is offered, and one that draws pictures is not.</summary>
    public bool OffersText => Modalities.WritesText(OutputModalities);

    public string Canonical => ModelNames.Canon(Id);
}

/// <summary>How the picker narrows the catalog.</summary>
public enum CatalogFilter
{
    All,

    /// <summary>Models with no price.</summary>
    Free,

    /// <summary>Models whose short answer costs a fraction of a cent.</summary>
    Cheap,
}

/// <summary>The public list of models and their prices: what a visitor can pick from behind a key, and what a call is expected to cost.</summary>
public interface IModelCatalog
{
    /// <summary>True once a list is in memory (from the network or from the copy kept on disk).</summary>
    bool Loaded { get; }

    /// <summary>The model a name stands for (the name need not be spelled the catalog's way), or null.</summary>
    CatalogModel? Find(string? name);

    /// <summary>Models whose id or name contains every word of the query, best first, offered for text.generate (the primary-role rule:
    /// a model that draws pictures is not in a text picker); no query lists the newest.</summary>
    IReadOnlyList<CatalogModel> Search(string? query, CatalogFilter filter = CatalogFilter.All, int limit = 40);
}

/// <summary>A catalog that knows nothing (tests, or a wallet that never got the list).</summary>
public sealed class EmptyCatalog : IModelCatalog
{
    public static readonly EmptyCatalog Instance = new();

    public bool Loaded => false;

    public CatalogModel? Find(string? name) => null;

    public IReadOnlyList<CatalogModel> Search(string? query, CatalogFilter filter = CatalogFilter.All, int limit = 40) => [];
}
