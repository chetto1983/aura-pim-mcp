using System.Buffers.Text;
using System.Text;
using System.Web;
using CalendarMcp.HttpServer.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Admin;

[TestClass]
public sealed class GoogleOAuthRelayTests
{
    private const string Relay = "https://chetto1983.github.io/aura-connect/google/callback/";
    private const string Callback = "https://192.168.101.158/admin/auth/google/callback";

    [TestMethod]
    public void State_CarriesTheInstallCallbackAfterTheNonce()
    {
        var state = GoogleOAuthRelay.CreateState(Callback);

        var dot = state.IndexOf('.');
        Assert.AreEqual(32, dot, "nonce is a 32-hex GUID");
        Assert.AreEqual(Callback, Encoding.UTF8.GetString(Base64Url.DecodeFromChars(state.AsSpan(dot + 1))));
        Assert.AreNotEqual(state, GoogleOAuthRelay.CreateState(Callback), "every state gets a fresh nonce");
    }

    [TestMethod]
    public void CodeChallenge_MatchesRfc7636AppendixB()
    {
        Assert.AreEqual(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            GoogleOAuthRelay.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [TestMethod]
    public void CodeVerifier_IsTheMinimumRfc7636LengthAndUnique()
    {
        var verifier = GoogleOAuthRelay.CreateCodeVerifier();

        Assert.AreEqual(43, verifier.Length);
        Assert.AreNotEqual(verifier, GoogleOAuthRelay.CreateCodeVerifier());
    }

    [TestMethod]
    public void InstallCallback_PrefersReturnBaseThenExternalBaseThenRequest()
    {
        var request = Request("http", "aura-pim-mcp:8080");

        Assert.AreEqual(Callback, GoogleOAuthRelay.InstallCallbackUrl("https://192.168.101.158", "http://localhost:8093", request));
        Assert.AreEqual("http://localhost:8093/admin/auth/google/callback", GoogleOAuthRelay.InstallCallbackUrl(null, "http://localhost:8093/", request));
        Assert.AreEqual("http://aura-pim-mcp:8080/admin/auth/google/callback", GoogleOAuthRelay.InstallCallbackUrl("", null, request));
    }

    [TestMethod]
    public void InstallCallback_KeepsTheForwardedHostAndItsPort()
    {
        var request = Request("http", "aura-pim-mcp:8080");
        request.Headers["X-Forwarded-Proto"] = "https";
        request.Headers["X-Forwarded-Host"] = "aura.example.com:8443";

        Assert.AreEqual("https://aura.example.com:8443/admin/auth/google/callback", GoogleOAuthRelay.InstallCallbackUrl(null, null, request));
    }

    [TestMethod]
    public void ReturnBase_KeepsOnlyTheOrigin()
    {
        Assert.AreEqual("https://aura.example.com:8443", GoogleOAuthRelay.OriginOf("https://aura.example.com:8443/"));
        Assert.AreEqual("http://localhost:8093", GoogleOAuthRelay.OriginOf("http://localhost:8093"));
    }

    [TestMethod]
    [DataRow("aura.example.com")]
    [DataRow("/relative")]
    [DataRow("ftp://aura.example.com")]
    [DataRow("javascript:alert(1)")]
    [DataRow("https://user:pass@aura.example.com")]
    [DataRow("https://aura.example.com/evil")]
    [DataRow("https://aura.example.com/?next=x")]
    [DataRow("https://aura.example.com/#x")]
    public void ReturnBase_RefusesAnythingButABareOrigin(string returnBase)
    {
        Assert.ThrowsExactly<ArgumentException>(() => GoogleOAuthRelay.OriginOf(returnBase));
    }

    [TestMethod]
    public void AuthorizationUrl_RedirectsThroughTheRelayWithPkce()
    {
        var manager = new GoogleOAuthManager(NullLogger<GoogleOAuthManager>.Instance);

        var url = new Uri(manager.GetAuthorizationUrl("tenant__google", "client-id", "client-secret", Relay, Callback));
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.AreEqual("accounts.google.com", url.Host);
        Assert.AreEqual(Relay, query["redirect_uri"]);
        Assert.AreEqual("client-id", query["client_id"]);
        Assert.AreEqual("S256", query["code_challenge_method"]);
        Assert.AreEqual(43, query["code_challenge"]!.Length);
        Assert.AreEqual("offline", query["access_type"]);
        var state = query["state"]!;
        Assert.AreEqual(Callback, Encoding.UTF8.GetString(Base64Url.DecodeFromChars(state.AsSpan(state.IndexOf('.') + 1))));
    }

    [TestMethod]
    public async Task Exchange_RefusesAStateThisServerNeverIssued()
    {
        var manager = new GoogleOAuthManager(NullLogger<GoogleOAuthManager>.Instance);
        var issued = HttpUtility.ParseQueryString(new Uri(manager.GetAuthorizationUrl("tenant__google", "id", "secret", Relay, Callback)).Query)["state"]!;
        var forged = issued[..33] + Base64Url.EncodeToString(Encoding.UTF8.GetBytes("https://evil.example/admin/auth/google/callback"));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.ExchangeCodeAsync(forged, "code", CancellationToken.None));
        StringAssert.Contains(ex.Message, "Invalid or expired OAuth state");
    }

    private static HttpRequest Request(string scheme, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }
}
