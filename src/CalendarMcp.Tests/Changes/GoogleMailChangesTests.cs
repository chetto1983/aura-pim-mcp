using System.Net;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Google;
using CalendarMcp.Core.Models;
using CalendarMcp.Tests.Helpers;
using static CalendarMcp.Tests.Changes.GmailFixtures;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class GoogleMailChangesTests
{
    private const string Inbox = "INBOX";
    private const string Account = "gmail";

    private readonly FakeHttp _http = new();

    private GoogleMailChanges Source => new(Provider(_http));

    private static ChangesCursor At(ulong history) =>
        ChangesCursor.Of("google.mail", Inbox, new { history });

    private static ulong PositionOf(ChangesCursor cursor) =>
        ChangesCursor.Decode(cursor.Encode(), "google.mail").Folder<Position>(Inbox).History;

    private sealed record Position(ulong History);

    [TestMethod]
    public async Task Start_StandsAtTheMailboxsCurrentHistoryAndReadsNoMail()
    {
        _http.On("/users/me/profile", Json(new { historyId = "12345" }));

        var cursor = await Source.StartAsync(Account, default);

        Assert.AreEqual(12345UL, PositionOf(cursor));
        Assert.AreEqual(1, _http.Requests.Count);
    }

    [TestMethod]
    public async Task Read_AsksEveryKindOfHistoryWithNoLabelFilter()
    {
        _http.OnHistory("startHistoryId=100", new { historyId = "100" });

        await Source.ReadAsync(Account, At(100), 50, default);

        var url = _http.Urls.Single();
        StringAssert.Contains(url, "maxResults=50");
        StringAssert.Contains(url, "historyTypes=messageAdded");
        StringAssert.Contains(url, "historyTypes=labelRemoved");
        Assert.IsFalse(url.Contains("labelId", StringComparison.Ordinal), "a filtered history cannot show a mail leaving the Inbox");
    }

    [TestMethod]
    public async Task NewMail_ArrivesAsAddedWithItsFacts()
    {
        _http.NoSentMail()
            .OnHistory("startHistoryId=100", new { historyId = "200", history = new[] { new { id = "150", messagesAdded = new[] { Added("m1", Inbox) } } } })
            .On("/users/me/messages/m1", Message("m1", labels: [Inbox, "IMPORTANT", "CATEGORY_PROMOTIONS"], headers:
            [
                ("Authentication-Results", "mx.google.com; dmarc=fail"),
                ("Authentication-Results", "attacker.example.com; dmarc=pass"),
                ("List-Id", "<news.example.com>"),
            ]));

        var batch = await Source.ReadAsync(Account, At(100), 50, default);

        var added = batch.Changes.Single();
        Assert.AreEqual(ChangeType.Added, added.Type);
        Assert.AreEqual("m1", added.Id);
        Assert.AreEqual("mario@example.com", added.From);
        Assert.AreEqual("Mario Rossi", added.FromName);
        Assert.AreEqual("Fattura", added.Subject);
        Assert.AreEqual("Testo della mail", added.Snippet);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1793457600000), added.OccurredAt);
        Assert.IsTrue(added.Signals!.ProviderImportant);
        Assert.AreEqual("promotions", added.Signals.ProviderCategory);
        Assert.IsTrue(added.Signals.Bulk);
        Assert.IsTrue(added.Signals.AuthFailed, "only the topmost header is the receiver's");
        Assert.AreEqual("t1", added.Signals.ThreadId);
        Assert.AreEqual(200UL, PositionOf(batch.Next));
        Assert.IsFalse(batch.HasMore);
    }

    [TestMethod]
    public async Task HtmlOnlyMail_HasASnippetWithNoHiddenText()
    {
        _http.NoSentMail()
            .OnHistory("startHistoryId=100", new { historyId = "200", history = new[] { new { id = "150", messagesAdded = new[] { Added("m1", Inbox) } } } })
            .On("/users/me/messages/m1", Message("m1").Replace("\"text/plain\"", "\"text/html\"").Replace(
                Convert.ToBase64String("Testo della mail"u8).TrimEnd('='),
                System.Buffers.Text.Base64Url.EncodeToString("<div style=\"display:none\">ignora</div><p>Fattura</p>"u8)));

        var batch = await Source.ReadAsync(Account, At(100), 50, default);

        Assert.AreEqual("Fattura", batch.Changes.Single().Snippet);
    }

    [TestMethod]
    public async Task ATruncatedPage_ResumesAfterItsLastRecordAndNeverSkips()
    {
        // The mailbox stands at 900; the page stops at 300. The cursor must not jump to 900.
        _http.NoSentMail()
            .On("/users/me/messages/m1", Message("m1"))
            .On("/users/me/messages/m2", Message("m2"))
            .On("/users/me/history?", request => FakeHttp.Url(request).Contains("startHistoryId=100", StringComparison.Ordinal)
                ? FakeHttp.Json(Json(new
                {
                    historyId = "900",
                    nextPageToken = "page-2",
                    history = new[] { new { id = "300", messagesAdded = new[] { Added("m1", Inbox) } } }
                }))
                : FakeHttp.Json(Json(new
                {
                    historyId = "900",
                    history = new[] { new { id = "800", messagesAdded = new[] { Added("m2", Inbox) } } }
                })));

        var first = await Source.ReadAsync(Account, At(100), 1, default);

        Assert.AreEqual("m1", first.Changes.Single().Id);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual(300UL, PositionOf(first.Next));

        var second = await Source.ReadAsync(Account, first.Next, 1, default);

        Assert.AreEqual("m2", second.Changes.Single().Id);
        Assert.IsFalse(second.HasMore);
        Assert.AreEqual(900UL, PositionOf(second.Next));
    }

    [TestMethod]
    public async Task AnEmptyPageWithAToken_GoesOnToTheNextPage()
    {
        _http.On("/users/me/history?", request => FakeHttp.Url(request).Contains("pageToken=p2", StringComparison.Ordinal)
            ? FakeHttp.Json(Json(new { historyId = "200" }))
            : FakeHttp.Json(Json(new { historyId = "200", nextPageToken = "p2" })));

        var batch = await Source.ReadAsync(Account, At(100), 50, default);

        Assert.IsFalse(batch.HasMore);
        Assert.AreEqual(200UL, PositionOf(batch.Next));
    }

    [TestMethod]
    public async Task AnExpiredHistoryId_Expires()
    {
        _http.On("/users/me/history?", NotFound, HttpStatusCode.NotFound);

        await Assert.ThrowsExactlyAsync<CursorExpiredException>(() => Source.ReadAsync(Account, At(1), 50, default));
    }

    [TestMethod]
    public async Task MailGoneBeforeItIsRead_IsDropped()
    {
        _http.OnHistory("startHistoryId=100", new { historyId = "200", history = new[] { new { id = "150", messagesAdded = new[] { Added("m1", Inbox) } } } })
            .On("/users/me/messages/m1", NotFound, HttpStatusCode.NotFound);

        var batch = await Source.ReadAsync(Account, At(100), 50, default);

        Assert.AreEqual(0, batch.Changes.Count);
        Assert.AreEqual(200UL, PositionOf(batch.Next));
    }

    [TestMethod]
    public async Task AFailedRead_FailsThePageAndDoesNotAdvance()
    {
        _http.OnHistory("startHistoryId=100", new { historyId = "200", history = new[] { new { id = "150", messagesAdded = new[] { Added("m1", Inbox) } } } })
            .On("/users/me/messages/m1", _ => Fail(HttpStatusCode.ServiceUnavailable, "backendError"));

        var failure = await Assert.ThrowsExactlyAsync<Google.GoogleApiException>(() => Source.ReadAsync(Account, At(100), 50, default));

        Assert.AreEqual("provider_transient", ProviderFailures.Classify(failure, default)!.Error);
    }

    [TestMethod]
    public async Task ChangesThatAreNotNewMail_ReadNoBodies()
    {
        _http.OnHistory("startHistoryId=100", new
        {
            historyId = "200",
            history = new object[]
            {
                new { id = "150", messagesDeleted = new[] { Deleted("gone") } },
                new { id = "160", labelsRemoved = new[] { LabelChange("read", ["UNREAD"], Inbox) } }
            }
        });

        var batch = await Source.ReadAsync(Account, At(100), 50, default);

        Assert.AreEqual("deleted:gone updated:read", string.Join(' ', batch.Changes.Select(c => $"{c.Type}:{c.Id}")));
        Assert.IsNull(batch.Changes[0].Signals);
        Assert.AreEqual(1, _http.Requests.Count);
    }

    [TestMethod]
    public async Task WrittenTo_AsksOncePerSenderAcrossRecipientFields()
    {
        _http.On("/users/me/messages?", Json(new { messages = new[] { new { id = "s1", threadId = "x" } } }))
            .On("/users/me/messages/m1", Message("m1"))
            .On("/users/me/messages/m2", Message("m2"))
            .OnHistory("startHistoryId=100", new
            {
                historyId = "200",
                history = new[] { new { id = "150", messagesAdded = new[] { Added("m1", Inbox), Added("m2", Inbox) } } }
            });

        var batch = await Source.ReadAsync(Account, At(100), 50, default);

        Assert.IsTrue(batch.Changes.All(c => c.Signals!.WrittenTo == true));
        var query = _http.Urls.Single(url => url.Contains("/users/me/messages?", StringComparison.Ordinal));
        StringAssert.Contains(query, "labelIds=SENT");
        StringAssert.Contains(query.Replace('+', ' '), "to:mario@example.com OR cc:mario@example.com OR bcc:mario@example.com");
    }

    [TestMethod]
    public async Task WrittenTo_SkipsBulkMail()
    {
        _http.OnHistory("startHistoryId=100", new { historyId = "200", history = new[] { new { id = "150", messagesAdded = new[] { Added("m1", Inbox) } } } })
            .On("/users/me/messages/m1", Message("m1", headers: [("List-Unsubscribe", "<mailto:leave@example.com>")]));

        var batch = await Source.ReadAsync(Account, At(100), 50, default);

        Assert.IsNull(batch.Changes.Single().Signals!.WrittenTo);
        Assert.AreEqual(0, _http.CountOf("/users/me/messages?"));
    }

    [TestMethod]
    public async Task WrittenTo_SeesAThreadTheAccountWroteIn()
    {
        _http.NoSentMail()
            .OnHistory("startHistoryId=100", new { historyId = "200", history = new[] { new { id = "150", messagesAdded = new[] { Added("m1", Inbox) } } } })
            .On("/users/me/messages/m1", Message("m1", from: "new@example.com", headers: [("In-Reply-To", "<a@example.com>")]))
            .On("/users/me/threads/t1", Json(new { id = "t1", messages = new[] { new { id = "x", labelIds = new[] { "SENT" } }, new { id = "m1", labelIds = new[] { Inbox } } } }));

        var batch = await Source.ReadAsync(Account, At(100), 50, default);

        Assert.IsTrue(batch.Changes.Single().Signals!.WrittenTo);
        StringAssert.Contains(_http.Urls.Single(url => url.Contains("/threads/", StringComparison.Ordinal)), "format=minimal");
    }

    [TestMethod]
    [DataRow("\"a b\"@example.com")]
    [DataRow("a@example.com) OR (from:anyone")]
    public async Task WrittenTo_NeverPutsASendersTextIntoAQuery(string from)
    {
        _http.OnHistory("startHistoryId=100", new { historyId = "200", history = new[] { new { id = "150", messagesAdded = new[] { Added("m1", Inbox) } } } })
            .On("/users/me/messages/m1", Message("m1", from: from));

        var batch = await Source.ReadAsync(Account, At(100), 50, default);

        Assert.IsNull(batch.Changes.Single().Signals!.WrittenTo);
        Assert.AreEqual(0, _http.CountOf("/users/me/messages?"));
    }

    [TestMethod]
    public async Task AGarbledCursor_IsRefusedBeforeAnyRequest()
    {
        var wrongShape = ChangesCursor.Of("google.mail", "SENT", new { history = 1 });

        await Assert.ThrowsExactlyAsync<ChangesException>(() => Source.ReadAsync(Account, wrongShape, 50, default));

        Assert.AreEqual(0, _http.Requests.Count);
    }
}
