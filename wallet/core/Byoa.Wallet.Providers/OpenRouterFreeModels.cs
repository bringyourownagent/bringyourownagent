using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// After a sign-in, the card offers a few free models from the public catalog behind the visitor's new key. The pick
/// rule, fixed on purpose: free variants only (the catalog's ids ending ":free" — OpenRouter's free tier, price zero
/// on both sides), text models only (the catalog keeps no others, and anything that smells of embeddings or a base
/// model is left out anyway), the biggest context first, then the most recently listed, a few at most, the ones the
/// visitor already added left out. He can untick any of them before they are added.
/// </summary>
public static class OpenRouterFreeModels
{
    public const int Count = 3;

    public static IReadOnlyList<CatalogModel> Pick(IModelCatalog catalog, IEnumerable<string> alreadyAdded, int count = Count)
    {
        var taken = alreadyAdded.Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return catalog.Search(null, CatalogFilter.Free, 200)
            .Where(m => m.Id.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
            .Where(m => !m.Id.Contains("embed", StringComparison.OrdinalIgnoreCase) && !m.Id.Contains("-base", StringComparison.OrdinalIgnoreCase))
            .Where(m => !taken.Contains(m.Id) && !taken.Contains(m.Canonical))
            .OrderByDescending(m => m.ContextLength)
            .ThenByDescending(m => m.Created)
            .Take(count)
            .ToList();
    }
}
