namespace Byoa.Wallet.Sources;

/// <summary>
/// What the wallet lets a run of the visitor's agent use, and what that makes the agent able to do (PROPOSAL-v2 3.4). The abilities are derived
/// from the tools, never declared by the visitor and never by a site: the wallet builds the command, so it knows. A site's wish for an ability
/// changes nothing about the run.
/// </summary>
public static class AgentProfile
{
    /// <summary>The tools of the agent's program that a run may use (Claude Code's names): the one list the command line and the abilities both come from.</summary>
    public static readonly IReadOnlyList<string> Tools = ["WebSearch", "WebFetch"];

    public const string WebSearch = "web.search";
    public const string WebRead = "web.read";

    /// <summary>Reserved (PROPOSAL-v2 2): pictures do not go to agents in this change, so it is never among the abilities.</summary>
    public const string ImageRead = "image.read";

    /// <summary>The abilities the tools give.</summary>
    public static readonly IReadOnlyList<string> Abilities = Tools.Select(AbilityOf).OfType<string>().ToList();

    static string? AbilityOf(string tool) => tool switch
    {
        "WebSearch" => WebSearch,
        "WebFetch" => WebRead,
        _ => null,
    };

    /// <summary>The words of the ability vocabulary (v1) that the wallet can answer for; an extension word (<c>x-*</c>) it cannot, and it is left alone.</summary>
    public static bool Knows(string ability) => ability is WebSearch or WebRead or ImageRead;

    /// <summary>For each ability a site asked about that the wallet knows: whether the agent has it. A word the wallet does not know is absent (unknown).</summary>
    public static IReadOnlyDictionary<string, bool> Verdicts(IReadOnlyList<string> asked)
    {
        var verdicts = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var ability in asked)
        {
            if (Knows(ability)) verdicts[ability] = Abilities.Contains(ability);
        }

        return verdicts;
    }

    /// <summary>The abilities a site asked about that the agent is known to lack, in the site's order.</summary>
    public static IReadOnlyList<string> Lacks(IReadOnlyList<string> asked) =>
        asked.Where(a => Knows(a) && !Abilities.Contains(a)).ToList();

    /// <summary>An ability in plain words, for the window and for the line the wallet adds to the agent's prompt.</summary>
    public static string Words(string ability) => ability switch
    {
        WebSearch => "search the web",
        WebRead => "read web pages",
        ImageRead => "look at pictures",
        _ => ability,
    };
}
