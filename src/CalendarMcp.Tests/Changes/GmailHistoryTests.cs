using CalendarMcp.Core.Changes.Google;
using Google.Apis.Gmail.v1.Data;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class GmailHistoryTests
{
    private const string Inbox = "INBOX";

    private static Message Mail(string id, params string[] labels) => new() { Id = id, LabelIds = labels };

    private static History Arrives(string id, params string[] labels) =>
        new() { MessagesAdded = [new HistoryMessageAdded { Message = Mail(id, labels) }] };

    private static History Labelled(string id, string label, params string[] now) =>
        new() { LabelsAdded = [new HistoryLabelAdded { Message = Mail(id, now), LabelIds = [label] }] };

    private static History Unlabelled(string id, string label, params string[] now) =>
        new() { LabelsRemoved = [new HistoryLabelRemoved { Message = Mail(id, now), LabelIds = [label] }] };

    private static History Gone(string id, params string[] labels) =>
        new() { MessagesDeleted = [new HistoryMessageDeleted { Message = Mail(id, labels) }] };

    private static string Read(params History[] records) =>
        string.Join(' ', GmailHistory.Net(records).Select(change => $"{change.Type}:{change.Id}"));

    [TestMethod]
    public void NewMailInTheInbox_IsAdded()
    {
        Assert.AreEqual("added:m1", Read(Arrives("m1", Inbox, "UNREAD")));
    }

    [TestMethod]
    public void MailOutsideTheInbox_IsNotWatched()
    {
        Assert.AreEqual(string.Empty, Read(Arrives("sent", "SENT"), Arrives("draft", "DRAFT"), Arrives("spam", "SPAM")));
    }

    [TestMethod]
    public void MailMovedIntoTheInbox_IsAdded()
    {
        Assert.AreEqual("added:m1", Read(Labelled("m1", Inbox, Inbox)));
    }

    [TestMethod]
    public void MailArchived_IsDeleted()
    {
        Assert.AreEqual("deleted:m1", Read(Unlabelled("m1", Inbox, "IMPORTANT")));
    }

    [TestMethod]
    public void MailBinned_IsDeletedOnce()
    {
        Assert.AreEqual("deleted:m1", Read(Labelled("m1", "TRASH", "TRASH"), Unlabelled("m1", Inbox, "TRASH")));
    }

    [TestMethod]
    public void MailDeletedForGood_IsDeleted()
    {
        Assert.AreEqual("deleted:m1", Read(Gone("m1")));
    }

    [TestMethod]
    public void MailDeletedFromElsewhere_IsNothingToTheInbox()
    {
        Assert.AreEqual(string.Empty, Read(Gone("m1", "TRASH")));
    }

    [TestMethod]
    public void AnotherLabelOnInboxMail_IsUpdated()
    {
        Assert.AreEqual("updated:m1", Read(Unlabelled("m1", "UNREAD", Inbox)));
        Assert.AreEqual("updated:m2", Read(Labelled("m2", "STARRED", Inbox, "STARRED")));
    }

    [TestMethod]
    public void ALabelOutsideTheInbox_IsNothing()
    {
        Assert.AreEqual(string.Empty, Read(Unlabelled("m1", "UNREAD", "SENT")));
    }

    [TestMethod]
    public void MailThatCameAndWentInOnePage_WasNeverThere()
    {
        Assert.AreEqual(string.Empty, Read(Arrives("m1", Inbox), Unlabelled("m1", Inbox, "TRASH"), Gone("m1", "TRASH")));
    }

    [TestMethod]
    public void AddedThenRead_StaysAdded()
    {
        Assert.AreEqual("added:m1", Read(Arrives("m1", Inbox, "UNREAD"), Unlabelled("m1", "UNREAD", Inbox)));
    }

    [TestMethod]
    public void ArchivedThenBack_IsUpdated()
    {
        Assert.AreEqual("updated:m1", Read(Unlabelled("m1", Inbox), Labelled("m1", Inbox, Inbox)));
    }

    [TestMethod]
    public void Changes_KeepTheOrderTheyHappenedIn()
    {
        Assert.AreEqual(
            "added:b deleted:a updated:c",
            Read(Arrives("b", Inbox), Gone("a"), Unlabelled("c", "UNREAD", Inbox)));
    }

    [TestMethod]
    public void ALabelRowWithNoMessage_CostsNothingElseOnThePage()
    {
        var damaged = new History
        {
            LabelsAdded = [new HistoryLabelAdded { LabelIds = [Inbox] }],
            LabelsRemoved = [new HistoryLabelRemoved { LabelIds = [Inbox] }]
        };

        Assert.AreEqual("added:m1", Read(damaged, Arrives("m1", Inbox)));
    }

    [TestMethod]
    public void OneRecord_CanHoldManyMessages()
    {
        var record = new History
        {
            MessagesAdded =
            [
                new HistoryMessageAdded { Message = Mail("a", Inbox) },
                new HistoryMessageAdded { Message = Mail("b", Inbox) }
            ]
        };

        Assert.AreEqual("added:a added:b", Read(record));
    }
}
