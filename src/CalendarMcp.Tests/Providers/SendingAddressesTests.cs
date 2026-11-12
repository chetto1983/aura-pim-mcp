using System.Net;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Tests.Changes;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

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

    private static SendingAddresses Addresses(IProviderServiceFactory factory) =>
        new(factory, NullLogger<SendingAddresses>.Instance);

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

    [TestMethod]
    public async Task GraphPrefersMailThenUserPrincipalName()
    {
        var withMail = new FakeHttp().On("/me", """{"mail":"davide@contoso.com","userPrincipalName":"davide_contoso#EXT#@x.onmicrosoft.com"}""");
        var account = TestData.CreateAccount(id: "m365", provider: "microsoft365");
        Assert.AreEqual("davide@contoso.com",
            await Addresses(new OneProvider(GraphFixtures.Provider(withMail))).AddressAsync(account, default));

        var withoutMail = new FakeHttp().On("/me", """{"mail":null,"userPrincipalName":"davide@outlook.com"}""");
        var outlook = TestData.CreateAccount(id: "m365", provider: "outlook.com");
        Assert.AreEqual("davide@outlook.com",
            await Addresses(new OneProvider(GraphFixtures.Outlook(withoutMail))).AddressAsync(outlook, default));
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

    [TestMethod]
    public async Task AnAccountThatCannotSendHasNoAddress()
    {
        var account = TestData.CreateAccount(id: "holidays", provider: "ics");
        var ics = new IIcsProviderServiceCreateExpectations();

        var address = await Addresses(new OneProvider(ics.Instance())).AddressAsync(account, default);

        Assert.IsNull(address);
    }
}
