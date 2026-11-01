using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Models;
using MailKit.Security;

namespace CalendarMcp.Tests.Changes;

/// <summary>The IMAP adapter against Dovecot, the server here that has CONDSTORE and real passwords.</summary>
[TestClass]
public class ImapCondStoreTests
{
    private static string Types(IEnumerable<Change> changes) => string.Join(' ', changes.Select(c => c.Type));

    [TestMethod]
    public async Task ASenderWithNoAddress_IsTheNameAlone()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var cursor = await mailbox.StartAsync();
        var mail = ImapMailbox.Mail("Senza indirizzo");
        mail.Headers["From"] = "Solo Nome";

        await mailbox.DeliverAsync(mail);
        var added = (await mailbox.ReadAsync(cursor)).Changes.Single();

        Assert.IsNull(added.From, "the server's placeholder names nobody");
        Assert.AreEqual("Solo Nome", added.FromName);

        var item = await mailbox.Provider.ReadItemAsync(mailbox.Account.Id, new ItemRef(added.Id, null), default);
        Assert.IsNull(item!.From, "an item reads as the change did");
        Assert.AreEqual("Solo Nome", item.FromName);
    }

    [TestMethod]
    public async Task AFlagChangeOnOldMail_IsUpdated()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var uid = await mailbox.DeliverAsync(ImapMailbox.Mail());
        var cursor = await mailbox.StartAsync();

        await mailbox.MarkReadAsync(uid);
        var batch = await mailbox.ReadAsync(cursor);

        var updated = batch.Changes.Single();
        Assert.AreEqual(ChangeType.Updated, updated.Type);
        StringAssert.EndsWith(updated.Id, $"/{uid}");
        Assert.IsNull(updated.Signals);
        Assert.AreEqual(0, (await mailbox.ReadAsync(batch.Next)).Changes.Count, "an update is told once");
    }

    [TestMethod]
    public async Task NewMail_IsAddedAndNotAlsoUpdated()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        await mailbox.DeliverAsync(ImapMailbox.Mail());
        var cursor = await mailbox.StartAsync();

        await mailbox.DeliverAsync(ImapMailbox.Mail("Nuova"));
        var batch = await mailbox.ReadAsync(cursor);

        Assert.AreEqual("added", Types(batch.Changes));
        Assert.AreEqual("Nuova", batch.Changes.Single().Subject);
    }

    [TestMethod]
    public async Task ASecondMail_DoesNotTurnTheFirstIntoAnUpdate()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var cursor = await mailbox.StartAsync();

        await mailbox.DeliverAsync(ImapMailbox.Mail("Prima"));
        var first = await mailbox.ReadAsync(cursor);
        await mailbox.DeliverAsync(ImapMailbox.Mail("Seconda"));
        var second = await mailbox.ReadAsync(first.Next);

        Assert.AreEqual("added", Types(first.Changes));
        Assert.AreEqual("added", Types(second.Changes), "arriving gave the first mail a mod-sequence, and that is no change");
    }

    [TestMethod]
    public async Task MailSeenAsNew_CanLaterBeUpdated()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var cursor = await mailbox.StartAsync();

        var uid = await mailbox.DeliverAsync(ImapMailbox.Mail());
        var first = await mailbox.ReadAsync(cursor);
        await mailbox.MarkReadAsync(uid);

        Assert.AreEqual("updated", Types((await mailbox.ReadAsync(first.Next)).Changes));
    }

    [TestMethod]
    public async Task ModSequences_SurviveOpeningTheSentFolder()
    {
        // MailKit forgets a folder has mod-sequences once another one is selected, and writtenTo selects Sent.
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var old = await mailbox.DeliverAsync(ImapMailbox.Mail());
        var cursor = await mailbox.StartAsync();

        await mailbox.DeliverAsync(ImapMailbox.Mail("Nuova"));
        var first = await mailbox.ReadAsync(cursor);
        await mailbox.MarkReadAsync(old);

        Assert.AreEqual("updated", Types((await mailbox.ReadAsync(first.Next)).Changes));
    }

    [TestMethod]
    public async Task NewMailReadBeforeTheWatchSawIt_IsStillAdded()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var cursor = await mailbox.StartAsync();

        var uid = await mailbox.DeliverAsync(ImapMailbox.Mail());
        await mailbox.MarkReadAsync(uid);

        Assert.AreEqual("added", Types((await mailbox.ReadAsync(cursor)).Changes));
    }

    [TestMethod]
    public async Task UpdatesWaitForNewMailToFinish()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var old = await mailbox.DeliverAsync(ImapMailbox.Mail("Vecchia"));
        var cursor = await mailbox.StartAsync();

        await mailbox.MarkReadAsync(old);
        await mailbox.DeliverAsync(ImapMailbox.Mail("Uno"));
        await mailbox.DeliverAsync(ImapMailbox.Mail("Due"));

        var first = await mailbox.ReadAsync(cursor, limit: 1);
        Assert.AreEqual("added", Types(first.Changes));
        Assert.IsTrue(first.HasMore);

        var second = await mailbox.ReadAsync(first.Next, limit: 1);
        Assert.AreEqual("added updated", Types(second.Changes), "the page holds the last new mail and then the update");
        Assert.IsFalse(second.HasMore);
    }

    [TestMethod]
    public async Task ManyUpdates_ArePagedWithoutLoss()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var uids = new List<uint>();
        for (var i = 0; i < 5; i++)
            uids.Add(await mailbox.DeliverAsync(ImapMailbox.Mail($"Mail {i}")));
        var cursor = await mailbox.StartAsync();

        foreach (var uid in uids)
            await mailbox.MarkReadAsync(uid);

        var seen = new List<string>();
        bool hasMore;
        do
        {
            var batch = await mailbox.ReadAsync(cursor, limit: 2);
            seen.AddRange(batch.Changes.Select(c => c.Id));
            cursor = batch.Next;
            hasMore = batch.HasMore;
        }
        while (hasMore);

        CollectionAssert.AreEquivalent(uids.Select(u => $"INBOX/{u}").ToArray(), seen.Select(id => $"INBOX/{id[(id.LastIndexOf('/') + 1)..]}").ToArray());
    }

    [TestMethod]
    public async Task ASingleStoreOverManyMails_IsNotSplit()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var uids = new List<uint>();
        for (var i = 0; i < 4; i++)
            uids.Add(await mailbox.DeliverAsync(ImapMailbox.Mail($"Mail {i}")));
        var cursor = await mailbox.StartAsync();

        await mailbox.MarkReadAsync([.. uids]);

        var seen = new HashSet<string>();
        bool hasMore;
        do
        {
            var batch = await mailbox.ReadAsync(cursor, limit: 1);
            seen.UnionWith(batch.Changes.Select(c => c.Id));
            cursor = batch.Next;
            hasMore = batch.HasMore;
        }
        while (hasMore);

        Assert.AreEqual(4, seen.Count);
    }

    [TestMethod]
    public async Task AnExpungeOnCondStore_IsStillDeleted()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var uid = await mailbox.DeliverAsync(ImapMailbox.Mail());
        var cursor = await mailbox.StartAsync();

        await mailbox.ExpungeAsync(uid);

        Assert.AreEqual("deleted", Types((await mailbox.ReadAsync(cursor)).Changes));
    }

    [TestMethod]
    public async Task AWrongPassword_IsAHardFailure()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        mailbox.Account.ProviderConfig["password"] = "not-the-password";

        var failure = await Assert.ThrowsExactlyAsync<AuthenticationException>(() => mailbox.StartAsync());

        Assert.AreEqual("provider_auth", ProviderFailures.Classify(failure, default)!.Error);
    }

    [TestMethod]
    public async Task ACursorFromBeforeCondStoreWasKnown_BeginsFromTheFoldersHighest()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var uid = await mailbox.DeliverAsync(ImapMailbox.Mail());
        var start = await mailbox.StartAsync();
        var noModSeq = ChangesCursor.Of("imap.mail", "INBOX", new
        {
            uidValidity = ChangesCursor.Decode(start.Encode(), "imap.mail").Folder<Saved>("INBOX").UidValidity,
            lastUid = uid,
            boundUid = uid,
            modSeq = (ulong?)null,
            known = uid.ToString()
        });

        await mailbox.MarkReadAsync(uid);
        var first = await mailbox.ReadAsync(noModSeq);
        await mailbox.MarkReadAsync(uid);

        Assert.AreEqual(0, first.Changes.Count, "with no position in the mod-sequences there is nothing to compare");
        Assert.AreEqual(0, (await mailbox.ReadAsync(first.Next)).Changes.Count);
    }

    // \Seen cannot show how a folder was opened, because BODY.PEEK keeps it by itself. \Recent can: a
    // read-write session takes it from a mail and EXAMINE must not (RFC 3501 sections 2.3.2 and 6.3.2).
    [TestMethod]
    public async Task WatchingAMailbox_OpensEveryFolderReadOnly()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        await mailbox.DropAsync("INBOX", ImapMailbox.Mail("Prima"));
        await mailbox.DropAsync(ImapMailbox.SentFolder, ImapMailbox.Mail("Inviata"));

        var cursor = await mailbox.StartAsync();
        await mailbox.DropAsync("INBOX", ImapMailbox.Mail("Seconda"));
        var batch = await mailbox.ReadAsync(cursor);
        await mailbox.Provider.ReadItemAsync(mailbox.Account.Id, new ItemRef(batch.Changes.Single().Id, null), default);

        Assert.AreEqual(2, await mailbox.RecentInAsync("INBOX"));
        Assert.AreEqual(1, await mailbox.RecentInAsync(ImapMailbox.SentFolder));
    }

    private sealed record Saved(uint UidValidity);
}
