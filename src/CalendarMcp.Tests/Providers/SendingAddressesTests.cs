using System.Net;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Tests.Changes;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;
using Rocks;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// The address each account sends from, so a client can show who a message will leave as. Measured on
/// 2026-11-12: a host reviewing a scheduled email could show only the opaque account id.
/// </summary>
[TestClass]
public class SendingAddressesTests
{
    private sealed class OneProvider(IProviderService provider) : IProviderServiceFactory
    {
        public int Calls { get; private set; }

        public IProviderService GetProvider(string accountType)
        {
            Calls++;
            return provider;
        }
    }

    private static SendingAddresses Addresses(IProviderServiceFactory factory, CapturingLogs? logs = null) =>
        new(factory, logs is null
            ? NullLogger<SendingAddresses>.Instance
            : LoggerFactory.Create(builder => builder.AddProvider(logs)).CreateLogger<SendingAddresses>());

    [TestMethod]
    public async Task ImapIsItsUsername()
    {
        var account = TestData.CreateAccount(id: "work", provider: "imap",
            providerConfig: new Dictionary<string, string> { ["username"] = "tecnico@example.com", ["password"] = "x" });
        var imap = new IImapProviderServiceCreateExpectations();

        var address = await Addresses(new OneProvider(imap.Instance())).AddressAsync(account, default);

        Assert.AreEqual("tecnico@example.com", address);
    }

    [TestMethod]
    public async Task GmailAsksTheProfile()
    {
        var http = new FakeHttp().On("/users/me/profile", """{"emailAddress":"someone@gmail.com","messagesTotal":1}""");
        var account = TestData.CreateAccount(id: "personal", provider: "google");

        var address = await Addresses(new OneProvider(GmailFixtures.Provider(http))).AddressAsync(account, default);

        Assert.AreEqual("someone@gmail.com", address);
    }

    // Measured 2026-11-25 on the lab VM: GET /me failed for every Microsoft account (Graph's documentation asks
    // for User.Read, which no scope list here carries). Each Graph case scripts /me with another address, so an
    // address read from Graph shows.
    private static FakeHttp GraphProfile() =>
        new FakeHttp().On("/me", """{"mail":"profile@contoso.com","userPrincipalName":"profile@contoso.com"}""");

    private static IEnumerable<(IProviderService Provider, AccountInfo Account)> GraphAccounts(FakeHttp http, string? username)
    {
        yield return (GraphFixtures.Provider(http, new StaticTokenAuthentication(username)),
            TestData.CreateAccount(id: "m365", provider: "microsoft365"));
        yield return (GraphFixtures.Outlook(http, new StaticTokenAuthentication(username)),
            TestData.CreateAccount(id: "m365", provider: "outlook.com"));
    }

    [TestMethod]
    public async Task GraphIsTheSignedInUsername()
    {
        foreach (var (provider, account) in GraphAccounts(GraphProfile(), "mario.rossi@contoso.com"))
        {
            var address = await Addresses(new OneProvider(provider)).AddressAsync(account, default);

            Assert.AreEqual("mario.rossi@contoso.com", address, account.Provider);
        }
    }

    [TestMethod]
    public async Task AGraphUsernameThatIsNoAddressIsLeftOut()
    {
        // preferred_username may be a phone number; MSAL falls back to the display name, then to a label.
        foreach (var username in new[] { "+393331234567", "Mario Rossi", "Missing from the token response", null })
        {
            foreach (var (provider, account) in GraphAccounts(GraphProfile(), username))
            {
                var address = await Addresses(new OneProvider(provider)).AddressAsync(account, default);

                Assert.IsNull(address, $"{account.Provider}: {username}");
            }
        }
    }

    [TestMethod]
    public async Task TheGraphLookupCallsNoGraph()
    {
        var http = GraphProfile();
        foreach (var (provider, account) in GraphAccounts(http, "mario.rossi@contoso.com"))
            await Addresses(new OneProvider(provider)).AddressAsync(account, default);

        Assert.IsEmpty(http.Requests);
    }

    // Review 2026-11-26: after a sign-out and a sign-in as someone else under the same account id, a cached
    // address showed the previous person as the From.
    [TestMethod]
    public async Task AGraphAccountSignedInAgainAsSomeoneElseShowsTheNewAddress()
    {
        var auth = new StaticTokenAuthentication("mario.rossi@contoso.com");
        var addresses = Addresses(new OneProvider(GraphFixtures.Provider(GraphProfile(), auth)));
        var account = TestData.CreateAccount(id: "m365", provider: "microsoft365");
        await addresses.AddressAsync(account, default);

        auth.Username = "lucia.bianchi@contoso.com";

        Assert.AreEqual("lucia.bianchi@contoso.com", await addresses.AddressAsync(account, default));
    }

    [TestMethod]
    public async Task AnImapAccountRepairedToAnotherUsernameShowsTheNewAddress()
    {
        var account = TestData.CreateAccount(id: "work", provider: "imap",
            providerConfig: new Dictionary<string, string> { ["username"] = "tecnico@example.com", ["password"] = "x" });
        var addresses = Addresses(new OneProvider(new IImapProviderServiceCreateExpectations().Instance()));
        await addresses.AddressAsync(account, default);

        account.ProviderConfig["username"] = "ufficio@example.com";

        Assert.AreEqual("ufficio@example.com", await addresses.AddressAsync(account, default));
    }

    [TestMethod]
    public async Task CachesAFoundAddress()
    {
        var http = new FakeHttp().On("/users/me/profile", """{"emailAddress":"someone@gmail.com"}""");
        var factory = new OneProvider(GmailFixtures.Provider(http));
        var addresses = Addresses(factory);
        var account = TestData.CreateAccount(id: "personal", provider: "google");

        await addresses.AddressAsync(account, default);
        await addresses.AddressAsync(account, default);

        Assert.AreEqual(1, http.Requests.Count(r => FakeHttp.Url(r).Contains("/profile", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AFailedLookupLeavesItOut()
    {
        var http = new FakeHttp().On("/users/me/profile", GmailFixtures.NotFound, HttpStatusCode.NotFound);
        var account = TestData.CreateAccount(id: "personal", provider: "google");

        var address = await Addresses(new OneProvider(GmailFixtures.Provider(http))).AddressAsync(account, default);

        Assert.IsNull(address);
    }

    // Measured 2026-11-25: the warning kept only "ODataError", so nothing said why the lookup failed.
    [TestMethod]
    public async Task AFailedGmailLookupLogsItsCodeAndNotItsMessage()
    {
        var http = new FakeHttp().On("/users/me/profile", GmailFixtures.NotFound, HttpStatusCode.NotFound);
        var account = TestData.CreateAccount(id: "personal", provider: "google");
        var logs = new CapturingLogs();

        await Addresses(new OneProvider(GmailFixtures.Provider(http)), logs).AddressAsync(account, default);

        var warning = logs.Entries.Single(entry => entry.Level == LogLevel.Warning).Line;
        StringAssert.Contains(warning, "GoogleApiException 404");
        Assert.DoesNotContain("Requested entity was not found", warning);
    }

    [TestMethod]
    public async Task AFailedMsalReadLogsItsCodeAndNotItsMessage()
    {
        var graph = new IM365ProviderServiceCreateExpectations();
        graph.Setups.SignedInUsernameAsync(Arg.Any<string>())
            .Callback(_ => throw new MsalClientException("client_id_must_be_guid", "mario.rossi@contoso.com: no client"));
        var account = TestData.CreateAccount(id: "m365", provider: "microsoft365");
        var logs = new CapturingLogs();

        await Addresses(new OneProvider(graph.Instance()), logs).AddressAsync(account, default);

        var warning = logs.Entries.Single(entry => entry.Level == LogLevel.Warning).Line;
        StringAssert.Contains(warning, "MsalClientException client_id_must_be_guid");
        Assert.DoesNotContain("mario.rossi@contoso.com", warning);
    }

    [TestMethod]
    public async Task AnAccountThatCannotSendHasNoAddress()
    {
        var account = TestData.CreateAccount(id: "holidays", provider: "ics");
        var ics = new IIcsProviderServiceCreateExpectations();

        var address = await Addresses(new OneProvider(ics.Instance())).AddressAsync(account, default);

        Assert.IsNull(address);
    }
}
