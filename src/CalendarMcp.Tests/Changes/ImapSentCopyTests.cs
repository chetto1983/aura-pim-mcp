using CalendarMcp.Core.Providers;
using CalendarMcp.Tests.Providers;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// Where a sent mail's copy lands. SMTP leaves no copy on most hosts, so the provider appends one, and the
/// folder differs by host: Dovecot marks <c>Sent</c> with SPECIAL-USE, GreenMail advertises no SPECIAL-USE
/// and nests folders under INBOX with a <c>.</c> separator, as Aruba does (<c>INBOX.Sent</c>, measured
/// 2026-11-23, when every copy was lost to a Gmail default).
/// </summary>
[TestClass]
public class ImapSentCopyTests
{
    private static Task<string> SendAsync(ImapMailbox mailbox) =>
        mailbox.Provider.SendEmailAsync(mailbox.Account.Id, "someone@example.org", "Prova", "Testo", bodyFormat: "text");

    [TestMethod]
    public async Task WithNoSentFolderSet_TheCopyLandsInTheFolderTheServerMarksSent()
    {
        await using var smtp = ScriptedMailServer.SmtpWithAuth();
        await using var mailbox = await ImapMailbox.OpenAsync(
            ImapServers.Dovecot, withSent: false, sentFolder: null, smtp: smtp.Address);

        await SendAsync(mailbox);

        Assert.AreEqual(1, await mailbox.CountInAsync("Sent"));
    }

    [TestMethod]
    public async Task OnAServerWithoutSpecialUse_TheCopyLandsInTheSentFolderUnderInbox()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(
            ImapServers.GreenMail, withSent: false, sentFolder: null, smtp: ImapServers.GreenMailSmtp);
        var sent = await mailbox.CreateFolderInInboxAsync("Sent");

        await SendAsync(mailbox);

        Assert.AreEqual("INBOX.Sent", sent);
        Assert.AreEqual(1, await mailbox.CountInAsync(sent));
    }

    [TestMethod]
    public async Task AConfiguredSentFolder_WinsOverTheOneTheHostWouldOffer()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(
            ImapServers.GreenMail, withSent: false, sentFolder: "Archivio", smtp: ImapServers.GreenMailSmtp);
        await mailbox.CreateFolderAsync("Archivio");
        var sent = await mailbox.CreateFolderInInboxAsync("Sent");

        await SendAsync(mailbox);

        Assert.AreEqual(1, await mailbox.CountInAsync("Archivio"));
        Assert.AreEqual(0, await mailbox.CountInAsync(sent));
    }

    [TestMethod]
    public async Task WithNoSentFolderAnywhere_TheMailStillGoesAndNoFolderIsMade()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(
            ImapServers.GreenMail, withSent: false, sentFolder: null, smtp: ImapServers.GreenMailSmtp);
        var before = await mailbox.FolderNamesAsync();

        var messageId = await SendAsync(mailbox);

        Assert.IsFalse(string.IsNullOrEmpty(messageId));
        CollectionAssert.AreEqual(before, await mailbox.FolderNamesAsync());
    }

    [TestMethod]
    [DataRow("INBOX|INBOX.Trash|INBOX.Sent", "INBOX.Sent", DisplayName = "Aruba-style, nested under INBOX")]
    [DataRow("INBOX|Posta inviata|Bozze", "Posta inviata", DisplayName = "an Italian name")]
    [DataRow("INBOX|Sent Items|Sent", "Sent", DisplayName = "the commoner name first")]
    [DataRow("INBOX|Archive|Drafts", null, DisplayName = "no sent folder")]
    [DataRow("INBOX|Clients.Sent Reports", null, DisplayName = "a name that only contains one")]
    public void SentFolderByName_ReadsTheLastSegmentOfTheName(string folders, string? expected)
    {
        var listed = folders.Split('|').Select(full => (FullName: full, Name: full.Split('.')[^1]));

        Assert.AreEqual(expected, ImapProviderService.SentFolderByName(listed));
    }
}
