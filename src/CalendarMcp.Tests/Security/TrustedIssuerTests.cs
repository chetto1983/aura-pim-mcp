using System.Security.Claims;
using CalendarMcp.HttpServer;
using CalendarMcp.HttpServer.Security;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Configuration;

namespace CalendarMcp.Tests.Security;

/// <summary>
/// What changes when more than one authorization server is trusted. These exist in both
/// directions on purpose: a trusted issuer must get in, and everything that merely looks
/// like one must not.
/// </summary>
[TestClass]
public sealed class TrustedIssuerTests
{
    private const string Home = "https://home.example";
    private const string Foreign = "https://foreign.example";

    private static McpOAuthOptions TwoIssuers() => new(
        [TrustedIssuer.Of(Home), TrustedIssuer.Of(Foreign)],
        "https://pim.example/",
        "mcp:tools");

    /// <summary>
    /// The attack the (issuer, subject) key prevents. RFC 7519 §4.1.2 promises `sub` is
    /// unique only within ONE issuer's namespace, so nothing stops a foreign account from
    /// being named after an existing tenant — keying on `sub` alone would hand it that
    /// person's mailboxes.
    /// </summary>
    [TestMethod]
    public void ForeignAccountNamedAfterATenantDoesNotGetThatTenant()
    {
        var oauth = TwoIssuers();

        var fromHome = oauth.TenantIdentityFor(Home, TestData.TenantA);
        var fromForeign = oauth.TenantIdentityFor(Foreign, TestData.TenantA);

        Assert.AreEqual(TestData.TenantA, fromHome, "the home subject stopped passing through");
        Assert.AreNotEqual(TestData.TenantA, fromForeign, "a foreign account reached the tenant of the same name");
        // And the foreign one must still be a usable tenant, or "different" just means broken.
        Assert.AreEqual(fromForeign, Core.Tenancy.TenantIdentity.Normalize(fromForeign));
    }

    /// <summary>
    /// Deterministic, not allocated: nothing is stored, so a caller that disconnects and
    /// reconnects — new token, new session, different MCP client — returns to the same
    /// tenant rather than to an empty one.
    /// </summary>
    [TestMethod]
    public void ForeignIdentitiesAreStableAndDoNotCollide()
    {
        var oauth = TwoIssuers();

        Assert.AreEqual(oauth.TenantIdentityFor(Foreign, "1043"), oauth.TenantIdentityFor(Foreign, "1043"));
        Assert.AreNotEqual(oauth.TenantIdentityFor(Foreign, "1043"), oauth.TenantIdentityFor(Foreign, "10430"));
        Assert.AreNotEqual(oauth.TenantIdentityFor(Foreign, "1043"), oauth.TenantIdentityFor("https://other.example", "1043"));
    }

    /// <summary>
    /// The derivation is RFC 4122 §4.3, not an ad-hoc hash. This pins one known vector, so
    /// a byte-order slip in the namespace swap cannot silently re-home every foreign tenant.
    /// </summary>
    [TestMethod]
    public void NameBasedIdentityFollowsRfc4122()
    {
        // Pinned against an independent RFC 4122 implementation (Python's uuid5) over the
        // same namespace and name, so a byte-order slip in the endianness swap cannot
        // silently re-home every foreign tenant while still producing a plausible GUID.
        Assert.AreEqual(
            "7fdae2c4-8f46-59ec-8eaf-efba7f4a1dd5",
            McpOAuthOptions.ForeignIdentityNamespace.ToString("D"));
        Assert.AreEqual(
            "a0e112d3-507b-5b3a-aac4-b779786abce4",
            TwoIssuers().TenantIdentityFor(Foreign, "1043"));

        // Guid.ToByteArray emits the first three fields little-endian, so the version
        // nibble lands at index 7 and the variant stays at index 8.
        var derived = Guid.Parse(TwoIssuers().TenantIdentityFor(Foreign, "1043")).ToByteArray();
        Assert.AreEqual(0x50, derived[7] & 0xF0, "not a version 5 UUID");
        Assert.AreEqual(0x80, derived[8] & 0xC0, "not an RFC 4122 variant");
    }

    [TestMethod]
    public void TrustedIssuersAreReadFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OAuth:Issuer"] = "https://home.example/",
            ["OAuth:MetadataAddress"] = "http://aura:9080/.well-known/oauth-authorization-server",
            ["OAuth:TrustedIssuers"] =
                " https://accounts.google.com=https://accounts.google.com/.well-known/openid-configuration , https://kc.example/realms/aura ,, ",
        }).Build();

        var oauth = McpOAuthOptions.FromConfiguration(configuration);

        CollectionAssert.AreEqual(
            new[] { Home, "https://accounts.google.com", "https://kc.example/realms/aura" },
            oauth.Issuers.Select(issuer => issuer.Issuer).ToArray());
        // The home issuer keeps its split-horizon metadata address: the issuer is the name
        // a client reaches, the metadata the name this container reaches.
        Assert.AreEqual("http://aura:9080/.well-known/oauth-authorization-server", oauth.Home.MetadataAddress);
        // No `=`, so the default discovery path applies — the same rule the home issuer follows.
        Assert.AreEqual(
            "https://kc.example/realms/aura/.well-known/oauth-authorization-server",
            oauth.Issuers[2].MetadataAddress);
    }

    [TestMethod]
    public void IssuerNamedMatchesExactlyAndToleratesOnlyATrailingSlash()
    {
        var oauth = TwoIssuers();

        Assert.IsNotNull(oauth.IssuerNamed(Foreign + "/"));
        Assert.IsNull(oauth.IssuerNamed("https://foreign.example.evil"));
        Assert.IsNull(oauth.IssuerNamed("foreign.example"));
        Assert.IsNull(oauth.IssuerNamed(null));
    }

    [TestMethod]
    public void RebindLeavesAHomePrincipalUntouchedAndResolvesFoggyOnes()
    {
        var oauth = TwoIssuers();

        var home = McpTenantClaims.Rebind(Principal(Home, TestData.TenantA), oauth);
        Assert.AreEqual(TestData.TenantA, home!.FindFirst("sub")!.Value);
        Assert.IsNull(home.FindFirst(McpTenantClaims.OriginalSubjectClaim));

        var foreign = McpTenantClaims.Rebind(Principal(Foreign, TestData.TenantA), oauth);
        Assert.AreNotEqual(TestData.TenantA, foreign!.FindFirst("sub")!.Value);
        Assert.AreEqual(TestData.TenantA, foreign.FindFirst(McpTenantClaims.OriginalSubjectClaim)!.Value,
            "the asserted subject must survive for audit");
        // The two callers downstream read exactly this claim and nothing else.
        Assert.AreEqual(foreign.FindFirst("sub")!.Value, Core.Tenancy.TenantIdentity.FromPrincipal(foreign));
    }

    private static ClaimsPrincipal Principal(string issuer, string subject) =>
        new(new ClaimsIdentity([new Claim("iss", issuer), new Claim("sub", subject)], "Bearer"));
}
