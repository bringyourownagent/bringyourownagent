using Byoa.Wallet.Engine;

namespace Byoa.Wallet.Tests;

/// <summary>The consent window shows a source once for all the languages that go to it (a site that lists four languages,
/// all read by one local model, used to show that model four times).</summary>
public class RouteGroupTests
{
    static RouteLine Local(string language, string name = "Model A", string? detail = "speed not measured yet") =>
        new(language, name + " · this computer", "Llama", false, true, detail);

    [Fact]
    public void LanguagesThatGoToTheSameSourceAreOneGroup()
    {
        var groups = RouteGroup.Of([Local("en"), Local("es"), Local("ru"), Local("sk")]);
        var group = Assert.Single(groups);
        Assert.Equal(["en", "es", "ru", "sk"], group.Languages);
        Assert.Equal("Model A · this computer", group.Route.SourceName);
    }

    [Fact]
    public void DifferentSourcesStayApartInTheOrderTheirFirstLanguageCame()
    {
        var groups = RouteGroup.Of([Local("ru", "Model R"), Local("sk", "Model S"), Local("en", "Model S"), Local("es", "Model R")]);
        Assert.Equal(["Model R · this computer", "Model S · this computer"], groups.Select(g => g.Route.SourceName));
        Assert.Equal(["ru", "es"], groups[0].Languages);
        Assert.Equal(["sk", "en"], groups[1].Languages);
    }

    [Fact]
    public void ThePriceTheNoteAndWhetherItFitsKeepSourcesApart()
    {
        var paid = Local("ru") with { Paid = true };
        var other = Local("sk", detail: "needs 2 GB first");
        var none = new RouteLine("en", "no source", "", false, false);
        Assert.Equal(4, RouteGroup.Of([Local("es"), paid, other, none]).Count);
    }

    [Fact]
    public void ARouteWithoutALanguageIsStillAGroup()
    {
        var group = Assert.Single(RouteGroup.Of([new RouteLine(null, "Model A · this computer", "Llama", false, true)]));
        Assert.Empty(group.Languages);
    }
}
