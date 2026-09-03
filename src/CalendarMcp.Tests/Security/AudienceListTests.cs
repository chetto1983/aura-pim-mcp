using CalendarMcp.HttpServer;
using Microsoft.Extensions.Configuration;

namespace CalendarMcp.Tests.Security;

/// <summary>
/// One server, several names. Measured 2026-09-03: this server advertises a resource
/// derived from the request, so a client on the host discovered http://127.0.0.1:8093 and
/// asked for a token bound to it, while validation compared that against the single
/// configured http://aura-pim-mcp:8080/ and answered IDX10214 on every request. Accepting
/// only the container name locks out every host client; accepting only the loopback name
/// locks out the in-container agent, whose grant is bound to the name it dials. Both must
/// hold at once — and neither may become "any name".
/// </summary>
[TestClass]
public sealed class AudienceListTests
{
    private const string Loopback = "http://127.0.0.1:8093";
    private const string Internal = "http://aura-pim-mcp:8080/";

    [TestMethod]
    public void TheFirstEntryIsCanonicalAndTheRestAreMerelyAccepted()
    {
        CollectionAssert.AreEqual(
            new[] { Loopback, Internal },
            McpOAuthOptions.SplitAudiences($" {Loopback} , {Internal} ,, ").ToArray());
    }

    [TestMethod]
    public void ASingleNameStaysASingleName()
    {
        CollectionAssert.AreEqual(new[] { Internal }, McpOAuthOptions.SplitAudiences(Internal).ToArray());
    }

    [TestMethod]
    public void AnEmptySettingFallsBackRatherThanAcceptingNothing()
    {
        CollectionAssert.AreEqual(
            new[] { McpOAuthOptions.DefaultResource },
            McpOAuthOptions.SplitAudiences("  ,  ").ToArray());
        CollectionAssert.AreEqual(
            new[] { McpOAuthOptions.DefaultResource },
            McpOAuthOptions.SplitAudiences(null).ToArray());
    }

    [TestMethod]
    public void ConfigurationYieldsTheCanonicalNameAndTheWholeList()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OAuth:Issuer"] = "http://127.0.0.1:9080",
            ["OAuth:Resource"] = $"{Loopback},{Internal}",
        }).Build();

        var oauth = McpOAuthOptions.FromConfiguration(configuration);

        Assert.AreEqual(Loopback, oauth.Resource, "the canonical name is not the first entry");
        CollectionAssert.AreEqual(new[] { Loopback, Internal }, oauth.AcceptedAudiences.ToArray());
    }

    /// <summary>
    /// Back-compat for every options object built with Resource alone — the other tests in
    /// this assembly do exactly that. The fallback must be the single canonical audience,
    /// never the empty list, which would accept no token at all.
    /// </summary>
    [TestMethod]
    public void OptionsNamingOneResourceKeepValidatingThatOne()
    {
        var oauth = new McpOAuthOptions([TrustedIssuer.Of("http://127.0.0.1:9080")], Internal, "mcp:tools");

        CollectionAssert.AreEqual(new[] { Internal }, oauth.AcceptedAudiences.ToArray());
    }
}
