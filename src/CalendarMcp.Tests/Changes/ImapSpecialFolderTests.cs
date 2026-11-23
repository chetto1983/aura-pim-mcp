using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Tests.Providers;
using MailKit;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// Where a sent mail's copy and a deleted mail land. Hosts name the Sent and Trash folders differently: Dovecot
/// marks them with SPECIAL-USE, GreenMail advertises no SPECIAL-USE and nests folders under INBOX with a
/// <c>.</c> separator, as Aruba does (<c>INBOX.Sent</c>, <c>INBOX.Trash</c>, measured 2026-11-23, when Gmail's
/// default names lost every sent copy and failed every delete there).
/// </summary>
[TestClass]
public class ImapSpecialFolderTests
{
    private static Task<string> SendAsync(ImapMailbox mailbox) =>
        mailbox.Provider.SendEmailAsync(mailbox.Account.Id, "someone@example.org", "Prova", "Testo", bodyFormat: "text");

    private static async Task DeleteTheOnlyMailAsync(ImapMailbox mailbox)
    {
        await mailbox.DeliverAsync(ImapMailbox.Mail("Da cancellare"));
        var mail = (await mailbox.Provider.GetEmailsAsync(mailbox.Account.Id)).Single();
        await mailbox.Provider.DeleteEmailAsync(mailbox.Account.Id, mail.Id);
    }

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
    public async Task ADelete_MovesTheMailToTheFolderTheServerMarksTrash()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);

        await DeleteTheOnlyMailAsync(mailbox);

        Assert.IsFalse((await mailbox.Provider.GetEmailsAsync(mailbox.Account.Id)).Any(), "the mail left the inbox");
        Assert.AreEqual(1, await mailbox.CountInAsync("Trash"));
    }

    [TestMethod]
    public async Task ADelete_OnAServerWithoutSpecialUse_MovesTheMailToTheTrashUnderInbox()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.GreenMail);
        var trash = await mailbox.CreateFolderInInboxAsync("Trash");

        await DeleteTheOnlyMailAsync(mailbox);

        Assert.IsFalse((await mailbox.Provider.GetEmailsAsync(mailbox.Account.Id)).Any(), "the mail left the inbox");
        Assert.AreEqual(1, await mailbox.CountInAsync(trash));
    }

    [TestMethod]
    public async Task ADelete_WithNoTrashAnywhere_IsRefusedAndTheMailStays()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.GreenMail);

        await Assert.ThrowsExactlyAsync<ProviderOperationException>(() => DeleteTheOnlyMailAsync(mailbox));

        Assert.AreEqual(1, (await mailbox.Provider.GetEmailsAsync(mailbox.Account.Id)).Count(), "the mail is still in the inbox");
    }

    [TestMethod]
    [DataRow(SpecialFolder.Sent, "INBOX|INBOX.Trash|INBOX.Sent", "INBOX.Sent", DisplayName = "Sent, nested under INBOX")]
    [DataRow(SpecialFolder.Sent, "INBOX|Posta inviata|Bozze", "Posta inviata", DisplayName = "Sent, an Italian name")]
    [DataRow(SpecialFolder.Sent, "INBOX|Sent Items|Sent", "Sent", DisplayName = "Sent, the commoner name first")]
    [DataRow(SpecialFolder.Sent, "INBOX|Archive|Drafts", null, DisplayName = "Sent, none")]
    [DataRow(SpecialFolder.Sent, "INBOX|Clients.Sent Reports", null, DisplayName = "Sent, a name that only contains one")]
    [DataRow(SpecialFolder.Trash, "INBOX|INBOX.Sent|INBOX.Trash", "INBOX.Trash", DisplayName = "Trash, nested under INBOX")]
    [DataRow(SpecialFolder.Trash, "INBOX|Cestino", "Cestino", DisplayName = "Trash, an Italian name")]
    [DataRow(SpecialFolder.Junk, "INBOX|INBOX.Spam", "INBOX.Spam", DisplayName = "Junk, named Spam")]
    public void FolderByName_ReadsTheLastSegmentOfTheName(SpecialFolder role, string folders, string? expected)
    {
        var listed = folders.Split('|').Select(full => (FullName: full, Name: full.Split('.')[^1]));

        Assert.AreEqual(expected, ImapProviderService.FolderByName(role, listed));
    }
}
