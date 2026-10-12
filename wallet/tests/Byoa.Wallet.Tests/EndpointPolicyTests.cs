using System.Net;
using Byoa.Wallet.Desktop;
using Byoa.Wallet.Registry;

namespace Byoa.Wallet.Tests;

public sealed class EndpointPolicyTests
{
    [Theory]
    [InlineData("", false, false, false)]
    [InlineData("ftp://127.0.0.1:1234", false, false, false)]
    [InlineData("http://user:pass@example.com", false, false, false)]
    [InlineData("http://0.0.0.0:1234", false, false, false)]
    [InlineData("http://169.254.169.254", false, false, false)]
    [InlineData("http://0.1.2.3", false, false, false)]
    [InlineData("http://[fe80::1]", false, false, false)]
    [InlineData("http://224.0.0.1", false, false, false)]
    [InlineData("http://255.255.255.255", false, false, false)]
    [InlineData("http://[::]", false, false, false)]
    [InlineData("http://localhost.", false, false, false)]
    [InlineData("http://printer", false, false, false)]
    [InlineData("http://127.0.0.1:0", false, false, false)]
    [InlineData("http://127.0.0.1:70000", false, false, false)]
    [InlineData("http://127.0.0.1:1234", true, true, false)]
    [InlineData("http://LOCALHOST:1234", true, true, false)]
    [InlineData("http://localhost", true, true, false)]
    [InlineData("http://0177.0.0.1:1234", true, true, false)]
    [InlineData("http://2130706433:1234", true, true, false)]
    [InlineData("http://0x7f000001:1234", true, true, false)]
    [InlineData("http://127.1:1234", true, true, false)]
    [InlineData("http://[::1]:1234", true, true, false)]
    [InlineData("http://[::ffff:127.0.0.1]:1234", true, true, false)]
    [InlineData("http://192.168.1.20:1234", true, false, true)]
    [InlineData("http://100.64.0.1:1234", true, false, true)]
    [InlineData("http://runtime.example", true, false, true)]
    [InlineData("https://runtime.example:443", true, false, true)]
    [InlineData("http://runtime.example:80", true, false, true)]
    public void EndpointVerdictIsExplicit(string endpoint, bool allowed, bool loopback, bool confirmation)
    {
        var result = EndpointPolicy.Check(endpoint);

        Assert.Equal(allowed, result.Allowed);
        Assert.Equal(loopback, result.IsLoopback);
        Assert.Equal(confirmation, result.NeedsConfirmation);
        if (endpoint.StartsWith("http://runtime", StringComparison.Ordinal))
            Assert.Equal("not encrypted", result.Warning);
    }

    [Fact]
    public async Task AConfirmedLiteralDoesNotNeedDnsAndKeepsItsWarning()
    {
        var result = await EndpointPolicy.CheckForConfirmationAsync("http://192.168.1.20:1234");

        Assert.True(result.Allowed);
        Assert.True(result.NeedsConfirmation);
        Assert.Equal("not encrypted", result.Warning);
    }

    [Fact]
    public void VisitorMayKeepAnExistingSingleLabelHomeServer()
    {
        var result = EndpointPolicy.Check("http://nas:11434/v1", agentSupplied: false);

        Assert.True(result.Allowed);
        Assert.False(result.NeedsConfirmation);
    }

    [Fact]
    public async Task ConfirmationCanonicalisesIdnAndRecordsTheResolvedClass()
    {
        var result = await EndpointPolicy.CheckForConfirmationAsync("https://bücher.example:443/v1", resolver:
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") }));

        Assert.True(result.Allowed);
        Assert.Contains("xn--", result.CanonicalEndpoint, StringComparison.Ordinal);
        Assert.Equal(["203.0.113.10"], result.ResolvedAddresses);
        Assert.Equal("public", result.AddressClass);
    }

    [Fact]
    public async Task ConfirmationRejectsUnsafeDnsAndUseTimeRejectsDriftToLoopback()
    {
        var refused = await EndpointPolicy.CheckForConfirmationAsync("https://model.example:443/v1",
            resolver: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        Assert.False(refused.Allowed);

        Assert.False(EndpointPolicy.IsAllowedAtUse(IPAddress.Loopback, "public", ["203.0.113.10"]));
        Assert.False(EndpointPolicy.IsAllowedAtUse(IPAddress.Parse("192.168.1.4"), "public", ["203.0.113.10"]));
        Assert.True(EndpointPolicy.IsAllowedAtUse(IPAddress.Parse("192.168.1.4"), "private", ["192.168.1.3"]));
        Assert.True(EndpointPolicy.IsAllowedAtUse(IPAddress.Loopback, "loopback", []));
    }

    [Theory]
    [InlineData("http://[::ffff:169.254.169.254]:1234")]
    [InlineData("http://[ff02::1]:1234")]
    [InlineData("http://[::]:1234")]
    [InlineData("http://example..com:1234")]
    [InlineData("http://example.com.")]
    [InlineData("http://example.com:0")]
    [InlineData("http://example.com:70000")]
    public void UnsafeEndpointFormsAreRefused(string endpoint)
    {
        Assert.False(EndpointPolicy.Check(endpoint).Allowed);
    }
}

public sealed class AgentIdentityTests
{
    [Fact]
    public void IdentityUsesObservedParentNotHelloNameOrPath()
    {
        var first = Byoa.Wallet.Registry.AgentIdentity.CreateObserved("trusted", "C:\\wallet\\byoa-mcp.exe", "C:\\Program Files\\Agent\\agent.exe");
        var sameParent = Byoa.Wallet.Registry.AgentIdentity.CreateObserved("hostile label", "C:\\somewhere\\byoa-mcp.exe", "C:\\Program Files\\Agent\\agent.exe");
        var wrapper = Byoa.Wallet.Registry.AgentIdentity.CreateObserved("trusted", "C:\\wallet\\byoa-mcp.exe", "C:\\Windows\\System32\\cmd.exe");
        var unknown = Byoa.Wallet.Registry.AgentIdentity.CreateObserved("trusted", "C:\\wallet\\byoa-mcp.exe", null);

        Assert.Equal(first.Id, sameParent.Id);
        Assert.NotEqual(first.Id, wrapper.Id);
        Assert.NotEqual(first.Id, unknown.Id);
        Assert.Equal("hostile label", sameParent.Name);
        Assert.Equal("C:\\Program Files\\Agent\\agent.exe", sameParent.ObservedParentPath);
        Assert.Equal("C:\\wallet\\byoa-mcp.exe", unknown.ExecutablePath);
        Assert.Null(unknown.ObservedParentPath);
    }

    [Fact]
    public void MissingParentIdentityIsFreshAndCannotBecomeAStoredApproval()
    {
        var first = Byoa.Wallet.Registry.AgentIdentity.CreateObserved("agent", "C:\\wallet\\byoa-mcp.exe", null, 42,
            DateTimeOffset.UnixEpoch);
        var second = Byoa.Wallet.Registry.AgentIdentity.CreateObserved("agent", "C:\\wallet\\byoa-mcp.exe", null, 42,
            DateTimeOffset.UnixEpoch);

        Assert.NotEqual(first.Id, second.Id);
        Assert.True(first.SessionOnly);
        Assert.True(first.FullAccessAllowed);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("node.exe")]
    [InlineData("python.exe")]
    [InlineData("pwsh.exe")]
    public void GeneralPurposeParentsDoNotGetFirstConnectionFullAccess(string image)
    {
        var identity = Byoa.Wallet.Registry.AgentIdentity.CreateObserved("agent", "C:\\wallet\\byoa-mcp.exe", "C:\\Windows\\" + image);

        Assert.True(identity.ParentIsInterpreter);
        Assert.False(identity.FullAccessAllowed);
    }

    [Fact]
    public void DisplayNameIsBoundedAndHasNoControlCharacters()
    {
        var identity = Byoa.Wallet.Registry.AgentIdentity.CreateObserved("  Agent\n\t" + new string('x', 100), "C:\\wallet\\byoa-mcp.exe", "C:\\agent.exe");

        Assert.True(identity.Name.Length <= 60);
        Assert.DoesNotContain(identity.Name, char.IsControl);
    }

    [Fact]
    public void RedactedSiteLabelsUseAnOpaquePerWalletKey()
    {
        var firstKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var secondKey = Enumerable.Repeat((byte)7, 32).ToArray();
        var alpha = AgentPipeServer.HmacSiteLabel("https://alpha.example", firstKey);
        var alphaAgain = AgentPipeServer.HmacSiteLabel("https://alpha.example", firstKey);
        var beta = AgentPipeServer.HmacSiteLabel("https://beta.example", firstKey);

        Assert.Equal(alpha, alphaAgain);
        Assert.NotEqual(alpha, beta);
        Assert.NotEqual(alpha, AgentPipeServer.HmacSiteLabel("https://alpha.example", secondKey));
        Assert.StartsWith("site-", alpha, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha.example", alpha, StringComparison.Ordinal);
    }
}
