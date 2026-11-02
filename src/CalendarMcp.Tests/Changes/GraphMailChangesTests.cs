using System.Net;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Graph;
using CalendarMcp.Core.Models;
using CalendarMcp.Tests.Helpers;
using static CalendarMcp.Tests.Changes.GraphFixtures;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class GraphMailChangesTests
{
    private const string Account = "m365";
    private const string Kind = "microsoft365.mail";
    private const string Folder = "inbox";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string CreatedLink = $"{Base}/me/mailFolders('inbox')/messages/delta?$deltatoken=created-old";
    private static readonly string UpdatedLink = $"{Base}/me/mailFolders('inbox')/messages/delta?$deltatoken=updated-old";
    private static readonly string DeletedLink = $"{Base}/me/mailFolders('inbox')/messages/delta?$deltatoken=deleted-old";

    private readonly FakeHttp _http = new();
    private readonly FixedClock _clock = new(Now);

    private GraphMailChanges Source => new(ChangesProviders.Microsoft365, Provider(_http), _clock);

    private sealed record Stream(string Link, string? Successor, DateTimeOffset OpenedAt, bool RoundDone);

    private sealed record Streams(Stream Deleted, Stream Created, Stream Updated);

    private static ChangesCursor Cursor(
        string? created = null, string? updated = null, string? deleted = null, DateTimeOffset? openedAt = null, string kind = Kind)
    {
        var at = openedAt ?? Now.AddHours(-2);
        return ChangesCursor.Of(kind, Folder, new Streams(
            new Stream(deleted ?? DeletedLink, null, at, true),
            new Stream(created ?? CreatedLink, null, at, true),
            new Stream(updated ?? UpdatedLink, null, at, true)));
    }

    private static Streams Saved(ChangesCursor cursor) => ChangesCursor.Decode(cursor.Encode(), Kind).Folder<Streams>(Folder);

    private FakeHttp Quiet() => _http
        .On("deltatoken=created-old", Delta(CreatedLink))
        .On("deltatoken=updated-old", Delta(UpdatedLink))
        .On("deltatoken=deleted-old", Delta(DeletedLink));

    [TestMethod]
    public async Task Start_OpensOneStreamPerChangeTypeFromNow()
    {
        _http.On(request => FakeHttp.Url(request).Contains("/messages/delta", StringComparison.Ordinal), request =>
            FakeHttp.Json(Delta($"{Base}/me/mailFolders('inbox')/messages/delta?$deltatoken={request.RequestUri!.Query.Length}")));

        var cursor = await Source.StartAsync(Account, default);

        CollectionAssert.AreEquivalent(
            new[] { "changeType=created", "changeType=updated", "changeType=deleted" },
            _http.Urls.Select(url => url.Split('&', '?').Single(p => p.StartsWith("changeType", StringComparison.Ordinal))).ToArray());
        foreach (var url in _http.Urls)
        {
            StringAssert.Contains(url, "receivedDateTime ge 2026-11-01T11:00:00Z");
            StringAssert.Contains(url, "$select=id");
        }
        Assert.IsNotNull(Saved(cursor).Created.Link);
    }

    [TestMethod]
    public async Task Start_WalksAnyPagesAndReadsNoMail()
    {
        _http.On("changeType=created", Next($"{Base}/me/mailFolders('inbox')/messages/delta?$skiptoken=more", Row("old")))
            .On("changeType=updated", Delta(UpdatedLink))
            .On("changeType=deleted", Delta(DeletedLink))
            .On("skiptoken=more", Delta(CreatedLink, Row("older")));

        var cursor = await Source.StartAsync(Account, default);

        Assert.AreEqual(CreatedLink, Saved(cursor).Created.Link);
        Assert.AreEqual(0, _http.CountOf("/me/messages/"));
    }

    [TestMethod]
    public async Task NewMail_IsAddedBecauseTheCreatedStreamSaysSo()
    {
        Quiet().NoSentItems()
            .On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("/me/messages/m1", Message("m1", importance: "high", classification: "other", headers:
            [
                ("Authentication-Results", "spf=fail; dkim=none; dmarc=fail action=none; compauth=fail reason=000"),
                ("List-Unsubscribe", "<mailto:leave@example.com>"),
            ]));

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        var added = batch.Changes.Single();
        Assert.AreEqual(ChangeType.Added, added.Type);
        Assert.AreEqual("mario@example.com", added.From);
        Assert.AreEqual("Mario Rossi", added.FromName);
        Assert.AreEqual("Fattura", added.Subject);
        Assert.AreEqual("Testo della mail", added.Snippet);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), added.OccurredAt);
        Assert.IsTrue(added.Signals!.ProviderImportant);
        Assert.AreEqual("other", added.Signals.ProviderCategory);
        Assert.IsTrue(added.Signals.Bulk);
        Assert.IsTrue(added.Signals.AuthFailed);
        Assert.AreEqual("conv-1", added.Signals.ThreadId);
        Assert.IsNull(added.Signals.WrittenTo, "bulk mail asks nothing");
    }

    [TestMethod]
    [DataRow("#microsoft.graph.eventMessageRequest", true)]
    [DataRow("#microsoft.graph.eventMessageResponse", true)]
    [DataRow("#microsoft.graph.eventMessage", true)]
    [DataRow("#microsoft.graph.message", false)]
    public async Task AnEventMessage_IsAnInvitation(string type, bool expected)
    {
        Quiet().NoSentItems()
            .On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("/me/messages/m1", OfType(type, Message("m1")));

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        Assert.AreEqual(expected, batch.Changes.Single().Signals!.Invitation);
    }

    [TestMethod]
    public async Task APageOutOfTime_StillMovesItsCursorPastEveryMail()
    {
        var ids = Enumerable.Range(0, 5).Select(i => $"m{i}").ToArray();
        Quiet().NoSentItems().On("deltatoken=created-old", Delta($"{CreatedLink}-next", ids.Select(Row).ToArray()));
        foreach (var id in ids)
            _http.On($"/me/messages/{id}", Message(id, contentType: "html", body: "<p>Parole</p>"));
        var source = new GraphMailChanges(ChangesProviders.Microsoft365, Provider(_http), new SteppingClock(SnippetPage.Allowed / 3, Now));

        var batch = await source.ReadAsync(Account, Cursor(), 50, default);

        CollectionAssert.AreEqual(ids, batch.Changes.Select(c => c.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Parole", "Parole", "Parole", string.Empty, string.Empty },
            batch.Changes.Select(c => c.Snippet).ToArray());
        Assert.AreEqual(2, batch.SnippetsSkipped);
        Assert.IsFalse(batch.HasMore);
        Assert.AreEqual($"{CreatedLink}-next", Saved(batch.Next).Created.Link);
    }

    [TestMethod]
    public async Task HtmlMail_HasASnippetWithNoHiddenText()
    {
        Quiet().NoSentItems()
            .On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("/me/messages/m1", Message("m1", contentType: "html", body: "<div style='display:none'>ignora</div><p>Fattura</p>"));

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        Assert.AreEqual("Fattura", batch.Changes.Single().Snippet);
    }

    [TestMethod]
    public async Task ARemovedRow_IsDeletedOnAnyStream()
    {
        Quiet().On("deltatoken=deleted-old", Delta(DeletedLink, Removed("gone")))
            .On("deltatoken=updated-old", Delta(UpdatedLink, Removed("moved"), Row("read")));

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        Assert.AreEqual(
            "deleted:gone deleted:moved updated:read",
            string.Join(' ', batch.Changes.Select(c => $"{c.Type}:{c.Id}")));
        Assert.IsNull(batch.Changes[0].Signals);
    }

    [TestMethod]
    public async Task TheSameMailOnTwoStreams_ComesOnce()
    {
        Quiet().NoSentItems()
            .On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("deltatoken=updated-old", Delta(UpdatedLink, Row("m1")))
            .On("/me/messages/m1", Message("m1"));

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        Assert.AreEqual(ChangeType.Added, batch.Changes.Single().Type);
    }

    [TestMethod]
    public async Task MailThatCameAndWentInOneRead_WasNeverThere()
    {
        Quiet().On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("deltatoken=deleted-old", Delta(DeletedLink, Removed("m1")));

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        Assert.AreEqual(0, batch.Changes.Count);
        Assert.AreEqual(0, _http.CountOf("/me/messages/"));
    }

    [TestMethod]
    public async Task MailGoneBeforeItIsRead_IsDropped()
    {
        Quiet().On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .OnError("/me/messages/m1", HttpStatusCode.NotFound, "ErrorItemNotFound");

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        Assert.AreEqual(0, batch.Changes.Count);
    }

    [TestMethod]
    public async Task ATruncatedRound_KeepsItsNextLinkAndNeverSkips()
    {
        var page2 = $"{Base}/me/mailFolders('inbox')/messages/delta?$skiptoken=page2";
        var after = $"{Base}/me/mailFolders('inbox')/messages/delta?$deltatoken=created-new";
        Quiet().NoSentItems()
            .On("deltatoken=created-old", Next(page2, Row("m1")))
            .On("skiptoken=page2", Delta(after, Row("m2")))
            .On("deltatoken=created-new", Delta(after))
            .On("/me/messages/m1", Message("m1"))
            .On("/me/messages/m2", Message("m2"));

        var first = await Source.ReadAsync(Account, Cursor(), 1, default);

        Assert.AreEqual("m1", first.Changes.Single().Id);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual(page2, Saved(first.Next).Created.Link);
        Assert.AreEqual(UpdatedLink, Saved(first.Next).Updated.Link);
        Assert.AreEqual(0, _http.CountOf("deltatoken=updated-old"), "the page is full, so the next stream waits");

        var second = await Source.ReadAsync(Account, first.Next, 1, default);

        Assert.AreEqual("m2", second.Changes.Single().Id);
        Assert.AreEqual(after, Saved(second.Next).Created.Link);
        Assert.IsTrue(second.HasMore, "the updated stream has not had its turn");

        var third = await Source.ReadAsync(Account, second.Next, 1, default);

        Assert.AreEqual(0, third.Changes.Count);
        Assert.IsFalse(third.HasMore);
    }

    [TestMethod]
    public async Task ThePageSize_FollowsTheLimit()
    {
        Quiet();

        await Source.ReadAsync(Account, Cursor(), 7, default);

        Assert.IsTrue(_http.Requests.All(r => r.Headers.GetValues("Prefer").Contains("odata.maxpagesize=7")));
    }

    [TestMethod]
    public async Task EveryReadOfAMail_AsksForAnIdThatSurvivesAMove()
    {
        _http.On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("deltatoken=updated-old", Delta(UpdatedLink))
            .On("deltatoken=deleted-old", Delta(DeletedLink))
            .On("/me/messages/m1", Message("m1"))
            .NoSentItems();

        await Source.ReadAsync(Account, Cursor(), 10, default);
        await Source.ReadItemAsync(Account, new ItemRef("m1", null), default);

        var reads = _http.Requests.Where(r => !FakeHttp.Url(r).Contains("sentitems", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(5, reads.Count);
        Assert.IsTrue(reads.All(r => r.Headers.GetValues("Prefer").Contains(LastingIds)));
    }

    [TestMethod]
    public async Task ANewStream_AsksForIdsThatSurviveAMove()
    {
        _http.On(request => FakeHttp.Url(request).Contains("/messages/delta", StringComparison.Ordinal), _ => FakeHttp.Json(Delta(CreatedLink)));

        await Source.StartAsync(Account, default);

        Assert.AreEqual(3, _http.Requests.Count);
        Assert.IsTrue(_http.Requests.All(r => r.Headers.GetValues("Prefer").Contains(LastingIds)));
    }

    [TestMethod]
    public async Task AMail_ComesBackUnderTheIdItWasAskedFor()
    {
        _http.On("/me/messages/stored", Message("lasting"));

        var facts = await Source.ReadItemAsync(Account, new ItemRef("stored", null), default);

        Assert.AreEqual("stored", facts!.Id);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Gone, "resyncRequired")]
    [DataRow(HttpStatusCode.BadRequest, "syncStateNotFound")]
    [DataRow(HttpStatusCode.NotFound, "syncStateNotFound")]
    public async Task AnExpiredLink_Expires(HttpStatusCode status, string code)
    {
        Quiet().OnError("deltatoken=created-old", status, code);

        await Assert.ThrowsExactlyAsync<CursorExpiredException>(() => Source.ReadAsync(Account, Cursor(), 50, default));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests, "provider_transient")]
    [DataRow(HttpStatusCode.ServiceUnavailable, "provider_transient")]
    [DataRow(HttpStatusCode.Unauthorized, "provider_auth")]
    [DataRow(HttpStatusCode.Forbidden, "permission_denied")]
    public async Task AProviderFailure_IsNamed(HttpStatusCode status, string expected)
    {
        Quiet().OnError("deltatoken=created-old", status, "anything");

        var failure = await Assert.ThrowsAsync<Exception>(() => Source.ReadAsync(Account, Cursor(), 50, default));

        Assert.AreEqual(expected, ProviderFailures.Classify(failure, default)!.Error);
    }

    [TestMethod]
    [DataRow("https://evil.example.com/steal")]
    [DataRow("http://graph.microsoft.com/v1.0/me/messages/delta")]
    [DataRow("https://graph.microsoft.com.evil.example.com/v1.0/delta")]
    [DataRow("https://graph.microsoft.com@evil.example.com/v1.0/delta")]
    [DataRow("https://user:pass@graph.microsoft.com/v1.0/delta")]
    [DataRow("https://graph.microsoft.com:8443/v1.0/delta")]
    [DataRow("/v1.0/me/messages/delta")]
    [DataRow("not a url")]
    public async Task AForgedLink_NeverGetsARequest(string link)
    {
        var failure = await Assert.ThrowsExactlyAsync<ChangesException>(
            () => Source.ReadAsync(Account, Cursor(created: link), 50, default));

        Assert.AreEqual("invalid_cursor", failure.Error);
        Assert.AreEqual(0, _http.Requests.Count, "the account's bearer token goes on every request");
    }

    [TestMethod]
    public async Task AForgedSuccessor_NeverGetsARequest()
    {
        var forged = ChangesCursor.Of(Kind, Folder, new Streams(
            new Stream(DeletedLink, null, Now, true),
            new Stream(CreatedLink, "https://evil.example.com/steal", Now, true),
            new Stream(UpdatedLink, null, Now, true)));

        await Assert.ThrowsExactlyAsync<ChangesException>(() => Source.ReadAsync(Account, forged, 50, default));

        Assert.AreEqual(0, _http.Requests.Count);
    }

    [TestMethod]
    public async Task ACursorWithAStreamMissing_IsRefusedBeforeAnyRequest()
    {
        var forged = ChangesCursor.Of(Kind, Folder, new { Deleted = new Stream(DeletedLink, null, Now, true) });

        var failure = await Assert.ThrowsExactlyAsync<ChangesException>(() => Source.ReadAsync(Account, forged, 50, default));

        Assert.AreEqual("invalid_cursor", failure.Error);
        Assert.AreEqual(0, _http.Requests.Count);
    }

    [TestMethod]
    public async Task OutlookCom_IsReadThroughItsOwnProvider()
    {
        Quiet().NoSentItems()
            .On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("/me/messages/m1", Message("m1"));
        var outlook = new GraphMailChanges(ChangesProviders.Outlook, GraphFixtures.Outlook(_http), _clock);

        var batch = await outlook.ReadAsync(Account, Cursor(kind: "outlook.mail"), 50, default);

        Assert.AreEqual("m1", batch.Changes.Single().Id);
        Assert.AreEqual("outlook.mail", batch.Next.Kind);
    }

    [TestMethod]
    public async Task AnAgedStream_IsReplacedWithNoGap()
    {
        var successor = $"{Base}/me/mailFolders('inbox')/messages/delta?$deltatoken=created-fresh";
        Quiet().NoSentItems()
            .On("changeType=created", Delta(successor))
            .On("changeType=updated", Delta(UpdatedLink))
            .On("changeType=deleted", Delta(DeletedLink))
            .On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("/me/messages/m1", Message("m1"));

        var batch = await Source.ReadAsync(Account, Cursor(openedAt: Now.AddDays(-2)), 50, default);

        Assert.AreEqual("m1", batch.Changes.Single().Id, "the old stream reports what arrived before the new one opened");
        Assert.AreEqual(successor, Saved(batch.Next).Created.Link);
        Assert.AreEqual(Now, Saved(batch.Next).Created.OpenedAt);
        var opened = _http.Urls.ToList().FindIndex(u => u.Contains("changeType=created", StringComparison.Ordinal));
        var lastRead = _http.Urls.ToList().FindIndex(u => u.Contains("deltatoken=created-old", StringComparison.Ordinal));
        Assert.IsTrue(opened < lastRead, "the successor opens first, so the two overlap");
    }

    [TestMethod]
    public async Task AStreamAgedMidRound_KeepsItsSuccessorUntilTheRoundEnds()
    {
        var page2 = $"{Base}/me/mailFolders('inbox')/messages/delta?$skiptoken=page2";
        var successor = $"{Base}/me/mailFolders('inbox')/messages/delta?$deltatoken=created-fresh";
        Quiet().NoSentItems()
            .On("changeType=created", Delta(successor))
            .On("changeType=updated", Delta(UpdatedLink))
            .On("changeType=deleted", Delta(DeletedLink))
            .On("deltatoken=created-old", Next(page2, Row("m1")))
            .On("skiptoken=page2", Delta(CreatedLink, Row("m2")))
            .On("/me/messages/m1", Message("m1"))
            .On("/me/messages/m2", Message("m2"));

        var first = await Source.ReadAsync(Account, Cursor(openedAt: Now.AddDays(-2)), 1, default);

        Assert.AreEqual(page2, Saved(first.Next).Created.Link);
        Assert.AreEqual(successor, Saved(first.Next).Created.Successor);

        var second = await Source.ReadAsync(Account, first.Next, 50, default);

        Assert.AreEqual("m2", second.Changes.Single().Id);
        Assert.AreEqual(successor, Saved(second.Next).Created.Link);
        Assert.IsNull(Saved(second.Next).Created.Successor);
        Assert.AreEqual(1, _http.CountOf("changeType=created"));
    }

    [TestMethod]
    public async Task WrittenTo_SearchesRecipientsInSentItemsOncePerSender()
    {
        Quiet().On("deltatoken=created-old", Delta(CreatedLink, Row("m1"), Row("m2")))
            .On("/mailFolders/sentitems/messages", Json(new { value = new[] { Row("s1") } }))
            .On("/me/messages/m1", Message("m1"))
            .On("/me/messages/m2", Message("m2"));

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        Assert.IsTrue(batch.Changes.All(c => c.Signals!.WrittenTo == true));
        var query = _http.Urls.Single(u => u.Contains("/mailFolders/sentitems/messages", StringComparison.Ordinal));
        StringAssert.Contains(query, "$search=\"recipients:mario@example.com\"");
    }

    [TestMethod]
    public async Task WrittenTo_SeesAThreadTheAccountWroteIn()
    {
        Quiet().On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("/me/messages/m1", Message("m1", address: "new@example.com", conversation: "conv'1", headers: [("In-Reply-To", "<a@example.com>")]))
            .On("/mailFolders/sentitems/messages", request => FakeHttp.Json(
                FakeHttp.Url(request).Contains("conversationId eq 'conv''1'", StringComparison.Ordinal)
                    ? Json(new { value = new[] { Row("s1") } })
                    : Json(new { value = Array.Empty<object>() })));

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        Assert.IsTrue(batch.Changes.Single().Signals!.WrittenTo);
    }

    [TestMethod]
    public async Task WrittenTo_NeverPutsASendersTextIntoASearch()
    {
        Quiet().On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("/me/messages/m1", Message("m1", address: "a\" OR subject:\"x@example.com"));

        var batch = await Source.ReadAsync(Account, Cursor(), 50, default);

        Assert.IsNull(batch.Changes.Single().Signals!.WrittenTo);
        Assert.AreEqual(0, _http.CountOf("/mailFolders/sentitems/messages"));
    }

    [TestMethod]
    public async Task AFailedWrittenToQuery_FailsThePage()
    {
        Quiet().On("deltatoken=created-old", Delta(CreatedLink, Row("m1")))
            .On("/me/messages/m1", Message("m1"))
            .OnError("/mailFolders/sentitems/messages", HttpStatusCode.ServiceUnavailable, "serviceNotAvailable");

        var failure = await Assert.ThrowsAsync<Exception>(() => Source.ReadAsync(Account, Cursor(), 50, default));

        Assert.AreEqual("provider_transient", ProviderFailures.Classify(failure, default)!.Error);
    }

    [TestMethod]
    public async Task AnOutlookCursor_IsNotAMicrosoft365One()
    {
        var outlook = new GraphMailChanges(ChangesProviders.Outlook, Provider(_http), _clock);

        Assert.AreEqual("outlook", outlook.Provider);
        _http.On("/messages/delta", Delta(CreatedLink));
        var cursor = await outlook.StartAsync(Account, default);

        Assert.ThrowsExactly<ChangesException>(() => ChangesCursor.Decode(cursor.Encode(), Kind));
    }
}
