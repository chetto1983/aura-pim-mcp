using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CalendarMcp.Core.Discovery;
using CalendarMcp.Core.Providers;
using CalendarMcp.Tests.Admin;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Discovery;

/// <summary>
/// Finding a mailbox's servers from its address in Thunderbird's order: preset, the domain's own autoconfig, ISPDB,
/// then the MX. Nothing here touches the network: the seams answer from recorded ISPDB XML, and the HTTP client is
/// driven through a fake handler or a <see cref="HostGuard"/> whose resolver is the test's.
/// </summary>
[TestClass]
public class MailDiscoveryTests
{
    private const string GoogleAppPasswords = "https://support.google.com/accounts/answer/185833";

    private readonly FakeAutoconfig _autoconfig = new();
    private readonly FakeMx _mx = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));

    private MailDiscovery NewDiscovery(IMemoryCache? cache = null) => new(
        _autoconfig, _mx, cache ?? new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 }), _clock,
        DiscoveryBudget.Default, NullLogger<MailDiscovery>.Instance);

    private Task<DiscoveryResult> DiscoverAsync(string email) => NewDiscovery().DiscoverAsync(email, default);

    private static DiscoveryResult Unknown => new("unknown");

    // ── Presets ─────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("gmail.com")]
    [DataRow("googlemail.com")]
    public async Task Preset_Gmail_IsGoogleWithAPasswordFallback(string domain)
    {
        var found = await DiscoverAsync($"person@{domain}");

        Assert.AreEqual(
            new DiscoveryResult("google", "preset", "imap.gmail.com", 993, "ssl", "smtp.gmail.com", 465, "ssl",
                $"person@{domain}", new AppPasswordHint(true, GoogleAppPasswords), PasswordFallback: true),
            found);
        Assert.IsEmpty(_autoconfig.Calls, "a preset asks nobody");
        Assert.IsEmpty(_mx.Calls);
    }

    [TestMethod]
    [DataRow("outlook.com")]
    [DataRow("hotmail.it")]
    [DataRow("live.com")]
    [DataRow("msn.com")]
    public async Task Preset_OutlookCom_HasNoPasswordFallback(string domain)
    {
        var found = await DiscoverAsync($"person@{domain}");

        Assert.AreEqual(new DiscoveryResult("outlook.com", "preset", Username: $"person@{domain}", PasswordFallback: false), found);
    }

    [TestMethod]
    public async Task Preset_Libero_IsImapWithItsHostsAndSecurity()
    {
        var found = await DiscoverAsync("person@libero.it");

        Assert.AreEqual(
            new DiscoveryResult("imap", "preset", "imapmail.libero.it", 993, "ssl", "smtp.libero.it", 465, "ssl", "person@libero.it"),
            found);
    }

    [TestMethod]
    public void Presets_EveryEntryCitesItsPageAndHasAUsableShape()
    {
        foreach (var preset in MailPresets.All)
        {
            Assert.IsTrue(preset.Source.StartsWith("https://", StringComparison.Ordinal), preset.Source);
            Assert.IsTrue(preset.Domains.Length + (preset.MxSuffixes?.Length ?? 0) > 0, preset.Source);
            Assert.Contains(preset.Username, new[] { "address", "localpart" }, preset.Source);
            if (preset.Kind == "imap")
            {
                Assert.IsNotNull(preset.ImapHost, preset.Source);
                Assert.Contains(preset.ImapSecurity, new[] { "ssl", "starttls" }, preset.Source);
                Assert.Contains(preset.SmtpSecurity, new[] { "ssl", "starttls" }, preset.Source);
            }
        }

        Assert.AreEqual(1, MailPresets.All.Count(preset => preset.PasswordFallback == true), "the fallback is Google's only (A7)");
    }

    // ── The domain's own autoconfig ─────────────────────────────────

    [TestMethod]
    public async Task Isp_AutoconfigOfTheDomain_WinsOverIspdb()
    {
        _autoconfig.Isp["example.org"] = AutoconfigParserTests.Fixture("starttls-587.xml");
        _autoconfig.Ispdb["example.org"] = AutoconfigParserTests.Fixture("yahoo-two-imap.xml");

        var found = await DiscoverAsync("person@example.org");

        Assert.AreEqual(
            new DiscoveryResult("imap", "isp", "imap.example.org", 143, "starttls", "smtp.example.org", 587, "starttls", "person@example.org"),
            found);
        CollectionAssert.AreEqual(new[] { "isp:example.org" }, _autoconfig.Calls);
    }

    [TestMethod]
    public async Task Isp_NeverSendsTheAddress()
    {
        _mx.Hosts["example.org"] = "mx1.mail.example.net";

        var found = await DiscoverAsync("person.name+news@example.org");

        Assert.AreEqual(Unknown, found);
        foreach (var sent in _autoconfig.Calls.Concat(_mx.Calls))
        {
            Assert.DoesNotContain("person", sent);
            Assert.DoesNotContain("@", sent);
        }

        var http = new FakeHttp().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await new AutoconfigClient(new HttpClient(http), NullLogger<AutoconfigClient>.Instance).FetchIspAsync("example.org", default);
        CollectionAssert.AreEqual(
            new[]
            {
                "https://autoconfig.example.org/mail/config-v1.1.xml",
                "https://example.org/.well-known/autoconfig/mail/config-v1.1.xml"
            },
            http.Urls.ToArray(),
            "https only, and no emailaddress parameter");
    }

    [TestMethod]
    public async Task Isp_RefusesAPrivateAddress()
    {
        var resolved = new List<string>();
        var connected = new List<IPEndPoint>();
        var guard = new HostGuard(
            (host, _) =>
            {
                resolved.Add(host);
                return Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") });
            },
            (endpoint, _) =>
            {
                connected.Add(endpoint);
                return Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused));
            });
        using var http = new HttpClient(AutoconfigClient.CreateHandler(guard));

        var xml = await new AutoconfigClient(http, NullLogger<AutoconfigClient>.Instance).FetchIspAsync("example.org", default);

        Assert.IsNull(xml);
        CollectionAssert.AreEqual(new[] { "autoconfig.example.org", "example.org" }, resolved);
        Assert.IsEmpty(connected, "a private address is never connected");
    }

    [TestMethod]
    public async Task Isp_DoesNotFollowRedirects()
    {
        Assert.IsFalse(AutoconfigClient.CreateHandler(new HostGuard()).AllowAutoRedirect);

        var http = new FakeHttp().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://elsewhere.example.net/config-v1.1.xml") }
        });
        var client = new AutoconfigClient(new HttpClient(http), NullLogger<AutoconfigClient>.Instance);

        Assert.IsNull(await client.FetchIspAsync("example.org", default));
        Assert.IsNull(await client.FetchIspdbAsync("example.org", default));
        Assert.IsFalse(http.Urls.Any(url => url.Contains("elsewhere", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Isp_APageThatIsNotAConfig_FallsToWellKnown()
    {
        var http = new FakeHttp()
            .On("autoconfig.example.org", _ => Body("<html><body>Welcome</body></html>"))
            .On(".well-known", _ => Body(AutoconfigParserTests.Fixture("starttls-587.xml")));

        var xml = await new AutoconfigClient(new HttpClient(http), NullLogger<AutoconfigClient>.Instance).FetchIspAsync("example.org", default);

        Assert.AreEqual(AutoconfigParserTests.Fixture("starttls-587.xml"), xml);
    }

    [TestMethod]
    public async Task Isp_AHostThatDoesNotResolve_IsAMiss_ARefusedConnectionIsAFailure()
    {
        var unresolved = new HostGuard(
            (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
            (_, _) => Task.FromException<Socket>(new AssertFailedException("never connected")));
        var refusing = new HostGuard(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.7") }),
            (_, _) => Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused)));
        using var unresolvedHttp = new HttpClient(AutoconfigClient.CreateHandler(unresolved));
        using var refusingHttp = new HttpClient(AutoconfigClient.CreateHandler(refusing));

        Assert.IsNull(await new AutoconfigClient(unresolvedHttp, NullLogger<AutoconfigClient>.Instance).FetchIspAsync("example.org", default));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new AutoconfigClient(refusingHttp, NullLogger<AutoconfigClient>.Instance).FetchIspAsync("example.org", default));
    }

    [TestMethod]
    public async Task Ispdb_AServerError_IsAFailure_ANotFoundIsAMiss()
    {
        var down = new FakeHttp().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var absent = new FakeHttp().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new AutoconfigClient(new HttpClient(down), NullLogger<AutoconfigClient>.Instance).FetchIspdbAsync("example.org", default));
        Assert.IsNull(await new AutoconfigClient(new HttpClient(absent), NullLogger<AutoconfigClient>.Instance).FetchIspdbAsync("example.org", default));
    }

    [TestMethod]
    public async Task Ispdb_ASourceThatCannotBeAsked_IsNotCachedAsAMiss()
    {
        var asked = 0;
        _autoconfig.IspdbHook = (_, _) => ++asked == 1
            ? Task.FromException<string?>(new HttpRequestException("ISPDB answered 503."))
            : Task.FromResult<string?>(AutoconfigParserTests.Fixture("starttls-587.xml"));
        var discovery = NewDiscovery();

        var first = await discovery.DiscoverAsync("person@example.org", default);
        var second = await discovery.DiscoverAsync("person@example.org", default);

        Assert.AreEqual(Unknown, first);
        Assert.AreEqual("ispdb", second.Source);
        Assert.AreEqual(2, asked);
    }

    private static HttpResponseMessage Body(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [TestMethod]
    public async Task Isp_ABodyPastTheCap_IsAMiss()
    {
        var big = AutoconfigParserTests.Fixture("starttls-587.xml") + new string(' ', AutoconfigClient.MaxBodyBytes);
        var http = new FakeHttp().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(big) });

        var xml = await new AutoconfigClient(new HttpClient(http), NullLogger<AutoconfigClient>.Instance).FetchIspdbAsync("example.org", default);

        Assert.IsNull(xml);
        CollectionAssert.AreEqual(new[] { "https://autoconfig.thunderbird.net/v1.1/example.org" }, http.Urls.ToArray());
    }

    // ── MX ──────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("aspmx.l.google.com")]
    [DataRow("alt1.aspmx.l.googlemail.com")]
    public async Task Mx_GoogleHosted_IsGoogle(string mx)
    {
        _mx.Hosts["example.org"] = mx;

        var found = await DiscoverAsync("person@example.org");

        Assert.AreEqual(
            new DiscoveryResult("google", "preset-mx", "imap.gmail.com", 993, "ssl", "smtp.gmail.com", 465, "ssl",
                "person@example.org", new AppPasswordHint(true, GoogleAppPasswords), PasswordFallback: true),
            found);
    }

    [TestMethod]
    public async Task Mx_OutlookConsumer_IsOutlookCom()
    {
        _mx.Hosts["example.org"] = "msn-com.olc.protection.outlook.com";

        var found = await DiscoverAsync("person@example.org");

        Assert.AreEqual(new DiscoveryResult("outlook.com", "preset-mx", Username: "person@example.org", PasswordFallback: false), found);
    }

    [TestMethod]
    [DataRow("example-org.mail.protection.outlook.com")]
    [DataRow("mx1.outlook.com")]
    public async Task Mx_Microsoft365_IsMicrosoft365(string mx)
    {
        _mx.Hosts["example.org"] = mx;

        var found = await DiscoverAsync("person@example.org");

        Assert.AreEqual(new DiscoveryResult("microsoft365", "preset-mx"), found);
        Assert.IsFalse(_autoconfig.Calls.Any(call => call.EndsWith("outlook.com", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Mx_UnderAPresetDomain_IsThatPreset()
    {
        _mx.Hosts["example.org"] = "mx00.ionos.it";

        var found = await DiscoverAsync("person@example.org");

        Assert.AreEqual(
            new DiscoveryResult("imap", "preset-mx", "imap.ionos.it", 993, "ssl", "smtp.ionos.it", 465, "ssl", "person@example.org"),
            found);
        CollectionAssert.AreEqual(new[] { "isp:example.org", "ispdb:example.org" }, _autoconfig.Calls, "the preset answers before any fetch for ionos.it");
    }

    [TestMethod]
    [DataRow("mx1.mail.example.org")]
    [DataRow("mail.example.org")]
    [DataRow("example.org")]
    public async Task Mx_SameBaseAsTheDomain_StopsAtUnknown(string mx)
    {
        _mx.Hosts["example.org"] = mx;
        _autoconfig.Ispdb["mail.example.org"] = AutoconfigParserTests.Fixture("yahoo-two-imap.xml");

        var found = await DiscoverAsync("person@example.org");

        Assert.AreEqual(Unknown, found);
        CollectionAssert.AreEqual(new[] { "isp:example.org", "ispdb:example.org" }, _autoconfig.Calls);
    }

    [TestMethod]
    public async Task Mx_TriesSuffixesFromLongest()
    {
        _mx.Hosts["example.org"] = "mx1.eu.mail.example.net";
        _autoconfig.Ispdb["example.net"] = AutoconfigParserTests.Fixture("yahoo-two-imap.xml");

        var found = await DiscoverAsync("person@example.org");

        CollectionAssert.AreEqual(
            new[]
            {
                "isp:example.org", "ispdb:example.org",
                "isp:eu.mail.example.net", "ispdb:eu.mail.example.net",
                "isp:mail.example.net", "ispdb:mail.example.net",
                "isp:example.net", "ispdb:example.net"
            },
            _autoconfig.Calls);
        Assert.AreEqual(
            new DiscoveryResult("imap", "ispdb-mx", "imap.example.org", 993, "ssl", "smtp.example.org", 465, "ssl", "person@example.org"),
            found);
    }

    [TestMethod]
    public async Task Mx_None_IsUnknown()
    {
        Assert.AreEqual(Unknown, await DiscoverAsync("person@example.org"));
        CollectionAssert.AreEqual(new[] { "example.org" }, _mx.Calls);
    }

    // ── Budgets, cache, the address ─────────────────────────────────

    [TestMethod]
    public async Task Discover_FallsThroughOnTimeout()
    {
        _autoconfig.IspHook = async (_, ct) =>
        {
            _clock.Advance(DiscoveryBudget.Default.Step);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };
        _autoconfig.Ispdb["example.org"] = AutoconfigParserTests.Fixture("starttls-587.xml");
        var discovery = NewDiscovery();

        var first = await discovery.DiscoverAsync("person@example.org", default);
        var second = await discovery.DiscoverAsync("person@example.org", default);

        Assert.AreEqual("ispdb", first.Source);
        Assert.AreEqual(first, second);
        CollectionAssert.AreEqual(
            new[] { "isp:example.org", "ispdb:example.org", "isp:example.org" }, _autoconfig.Calls,
            "a timeout is not cached as a miss; the ISPDB hit is");
    }

    [TestMethod]
    public async Task Discover_OutOfOverallTime_IsUnknown()
    {
        _autoconfig.IspHook = async (_, ct) =>
        {
            _clock.Advance(DiscoveryBudget.Default.Overall);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };
        _autoconfig.Ispdb["example.org"] = AutoconfigParserTests.Fixture("starttls-587.xml");

        Assert.AreEqual(Unknown, await DiscoverAsync("person@example.org"));
        CollectionAssert.AreEqual(new[] { "isp:example.org" }, _autoconfig.Calls);
        Assert.IsEmpty(_mx.Calls);
    }

    [TestMethod]
    public async Task Discover_CallerCancellation_IsNotAVerdict()
    {
        using var cancelled = new CancellationTokenSource();
        _autoconfig.IspHook = async (_, ct) =>
        {
            await cancelled.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => NewDiscovery().DiscoverAsync("person@example.org", cancelled.Token));
    }

    [TestMethod]
    public async Task Discover_CachesHitsAndMisses()
    {
        _autoconfig.Ispdb["example.org"] = AutoconfigParserTests.Fixture("starttls-587.xml");
        var discovery = NewDiscovery();

        await discovery.DiscoverAsync("first@example.org", default);
        var second = await discovery.DiscoverAsync("second@example.org", default);
        await discovery.DiscoverAsync("person@example.net", default);
        await discovery.DiscoverAsync("person@example.net", default);

        Assert.AreEqual("second@example.org", second.Username, "the XML is cached, not the answer");
        CollectionAssert.AreEqual(
            new[] { "isp:example.org", "ispdb:example.org", "isp:example.net", "ispdb:example.net" }, _autoconfig.Calls);
    }

    [TestMethod]
    public async Task Discover_UppercaseAndIdnDomain_AreNormalised()
    {
        _autoconfig.Isp["xn--bcher-kva.example"] = AutoconfigParserTests.Fixture("emaildomain-hostname.xml");

        var idn = await DiscoverAsync("Persona@Bücher.EXAMPLE");
        var upper = await DiscoverAsync("PERSON@GMAIL.COM");
        var dotted = await DiscoverAsync("person@libero.it.");

        Assert.AreEqual("mail.xn--bcher-kva.example", idn.ImapHost);
        Assert.AreEqual("persona@xn--bcher-kva.example", idn.Username);
        Assert.AreEqual(("google", "person@gmail.com"), (upper.Kind, upper.Username));
        Assert.AreEqual(("preset", "person@libero.it"), (dotted.Source, dotted.Username), "a final dot is dropped before the lookup");
        Assert.AreEqual("isp:xn--bcher-kva.example", _autoconfig.Calls[0]);
    }

    [TestMethod]
    public async Task Discover_PlusTagIsStrippedFromTheUsername()
    {
        _autoconfig.Isp["example.org"] = AutoconfigParserTests.Fixture("starttls-587.xml");

        Assert.AreEqual("person@gmail.com", (await DiscoverAsync("person+news@gmail.com")).Username);
        Assert.AreEqual("person@example.org", (await DiscoverAsync("person+news@example.org")).Username);
    }

    // ── POST /admin/discover ────────────────────────────────────────

    [TestMethod]
    [DoNotParallelize]
    [DataRow("")]
    [DataRow("not-an-address")]
    [DataRow("person@localhost")]
    [DataRow("person@[127.0.0.1]")]
    [DataRow("person@exa mple.org")]
    [DataRow("@example.org")]
    public async Task Discover_BadAddress_Is400(string email)
    {
        await using var pipeline = new AdminPipeline(verifier: null);

        var (status, body) = await pipeline.PostAsync("/admin/discover", new { email });

        Assert.AreEqual(400, status);
        Assert.AreEqual("""{"error":"invalid_email"}""", body.GetRawText());
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Discover_TheRegisteredClient_IsGuardedAndFollowsNoRedirect()
    {
        await using var pipeline = new AdminPipeline(verifier: null);
        var handlers = pipeline.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        var clients = pipeline.Services.GetRequiredService<IHttpClientFactory>();

        HttpMessageHandler handler = handlers.CreateHandler(AutoconfigClient.HttpClientName);
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;

        var primary = handler as SocketsHttpHandler;
        Assert.IsNotNull(primary, "the primary handler is the guarded one");
        Assert.IsFalse(primary.AllowAutoRedirect);
        Assert.IsFalse(primary.UseProxy);
        Assert.IsNotNull(primary.ConnectCallback);
        Assert.AreEqual(AutoconfigClient.Timeout, clients.CreateClient(AutoconfigClient.HttpClientName).Timeout);
        Assert.IsNotNull(pipeline.Services.GetRequiredService<MailDiscovery>());
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Discover_Endpoint_AnswersCamelCaseWithoutNulls()
    {
        await using var pipeline = new AdminPipeline(verifier: null);

        var (status, body) = await pipeline.PostAsync("/admin/discover", new { email = "person@libero.it" });

        Assert.AreEqual(200, status);
        Assert.AreEqual(
            JsonSerializer.Serialize(new
            {
                kind = "imap", source = "preset", imapHost = "imapmail.libero.it", imapPort = 993, imapSecurity = "ssl",
                smtpHost = "smtp.libero.it", smtpPort = 465, smtpSecurity = "ssl", username = "person@libero.it"
            }),
            body.GetRawText());
    }
}
