using Byoa.Wallet.Desktop;
using Byoa.Wallet.Mcp;

namespace Byoa.Wallet.Tests;

public sealed class AgentToolCatalogTests
{
    [Fact]
    public void EveryPublishedToolHasAnExplicitWalletAccessClass()
    {
        var published = ToolCatalog.All()
            .Select(node => node!["name"]!.GetValue<string>())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var classified = AgentPipeServer.ClassifiedTools
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(published, classified);
        Assert.All(published, name => Assert.True(AgentPipeServer.IsClassifiedTool(name)));
    }

    [Theory]
    [InlineData("list_sites")]
    [InlineData("set_site_access")]
    [InlineData("set_limits")]
    public void FullAccessToolsAreMarkedAsSuch(string name)
    {
        Assert.True(AgentPipeServer.RequiresFullAccess(name));
        Assert.True(AgentPipeServer.IsClassifiedTool(name));
    }
}
