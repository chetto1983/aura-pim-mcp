using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Imap;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Security;
using CalendarMcp.Tests.Helpers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// Nothing signs a cursor or an item, so a 500 here tells Aura to retry something that can never work.
/// The host does not resolve: each of these has to be refused before the server is asked.
/// </summary>
[TestClass]
public class ForgedInputTests
{
    private const string Mine = "imap";

    private static AccountInfo Account() => TestData.CreateAccount(id: Mine, provider: "imap", providerConfig: new Dictionary<string, string>
    {
        ["imapHost"] = "imap.invalid",
        ["smtpHost"] = "imap.invalid",
        ["username"] = "someone@example.com",
        ["password"] = "secret",
        ["inboxFolder"] = "INBOX"
    });

    private static ChangesPipeline Imap()
    {
        var account = Account();
        return new ChangesPipeline([account],
        [
            new ImapProviderService(
                new SingleAccountRegistry(account),
                new PasswordProtector(new EphemeralDataProtectionProvider()),
                NullLogger<ImapProviderService>.Instance)
        ]);
    }

    [TestMethod]
    [DataRow(uint.MaxValue, "", "no UID can follow the last one")]
    [DataRow(1u, "0", "no mail has UID 0")]
    [DataRow(1u, "0:5", "nor does a range starting there")]
    [DataRow(1u, "7,0", "nor one further along")]
    public async Task AForgedPosition_IsTheCallersError(uint lastUid, string known, string why)
    {
        await using var pim = Imap();
        var cursor = ChangesCursor.Of("imap.mail", "INBOX", new ImapPosition(1, lastUid, 0, null, known)).Encode();

        var (status, body) = await pim.PostAsync("/admin/changes", new { accountId = Mine, resource = "mail", cursor });

        Assert.AreEqual(400, status, why);
        Assert.AreEqual("invalid_cursor", body.GetProperty("error").GetString());
    }

    [TestMethod]
    [DataRow(" ", "a blank id")]
    [DataRow("\t\n", "another blank id")]
    [DataRow("INBOX/1/0", "UID 0, which no mail has")]
    public async Task AForgedItem_IsTheCallersError(string id, string why)
    {
        await using var pim = Imap();

        var (status, body) = await pim.PostAsync("/admin/items", new { accountId = Mine, resource = "mail", items = new[] { new { id } } });

        Assert.AreEqual(400, status, why);
        Assert.AreEqual("invalid_items", body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task ANullItem_IsTheCallersError()
    {
        await using var pim = Imap();

        var (status, body) = await pim.PostAsync("/admin/items", new { accountId = Mine, resource = "mail", items = new object?[] { null } });

        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_items", body.GetProperty("error").GetString());
    }

    /// <summary>A key ring lost or rotated away leaves a stored password nothing can read. Retrying changes nothing.</summary>
    [TestMethod]
    public async Task APasswordThatCannotBeDecrypted_IsAHardFailureNamedForTheAccount()
    {
        var account = Account();
        account.ProviderConfig["password"] = new PasswordProtector(new EphemeralDataProtectionProvider()).Protect("secret");
        var other = new PasswordProtector(new EphemeralDataProtectionProvider());
        await using var imap = new ImapProviderService(new SingleAccountRegistry(account), other, NullLogger<ImapProviderService>.Instance);
        await using var pim = new ChangesPipeline([account], [imap]);

        var (status, body) = await pim.PostAsync("/admin/changes", new { accountId = Mine, resource = "mail" });

        Assert.AreEqual(502, status);
        Assert.AreEqual("provider_auth", body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task ABlankCalendarId_IsTheCallersError()
    {
        var calendar = new ScriptedSource(ChangesProviders.Google, ChangeResource.Calendar) { Item = _ => null };
        await using var pim = new ChangesPipeline([TestData.CreateAccount(id: Mine, provider: "google")], [calendar]);

        var (status, body) = await pim.PostAsync("/admin/items", new { accountId = Mine, resource = "calendar", items = new[] { new { id = "e1", calendarId = " " } } });

        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_items", body.GetProperty("error").GetString());
    }
}
