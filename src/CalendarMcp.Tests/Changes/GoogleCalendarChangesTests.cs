using System.Net;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Google;
using CalendarMcp.Core.Models;
using CalendarMcp.Tests.Helpers;
using static CalendarMcp.Tests.Changes.GmailFixtures;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class GoogleCalendarChangesTests
{
    private const string Account = "google";
    private const string List = "/users/me/calendarList";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeHttp _http = new();
    private readonly FixedClock _clock = new(Now);

    private GoogleCalendarChanges Source => new(Provider(_http), _clock);

    private static string Events(string calendar) => $"/calendars/{calendar}/events";

    private static object Calendar(string id, string role = "owner") => new { id, accessRole = role };

    private static string Calendars(params object[] items) => Json(new { items });

    private static object Item(
        string id, string created = "2026-10-01T09:00:00Z", string status = "confirmed",
        string start = "2026-11-02T09:00:00+01:00", string end = "2026-11-02T10:00:00+01:00") => new
        {
            id,
            status,
            created,
            updated = "2026-11-01T11:59:00Z",
            summary = "Riunione",
            organizer = new { email = "capo@example.com", self = false },
            attendees = new[]
            {
                new { email = "capo@example.com", self = false, responseStatus = "accepted" },
                new { email = "io@example.com", self = true, responseStatus = "needsAction" }
            },
            start = new { dateTime = start },
            end = new { dateTime = end }
        };

    private static ChangesCursor Cursor(params (string Id, string Sync)[] calendars) => ChangesCursor.Of(
        "google.calendar",
        calendars.Select(c => new KeyValuePair<string, object>(c.Id, new { sync = c.Sync, page = (string?)null, since = Now.AddHours(-1) })));

    private sealed record Saved(string Sync, string? Page);

    private static Saved SavedOf(ChangesCursor cursor, string calendar) =>
        ChangesCursor.Decode(cursor.Encode(), "google.calendar").Folder<Saved>(calendar);

    private static string[] Ids(ChangesCursor cursor) =>
        ChangesCursor.Decode(cursor.Encode(), "google.calendar").Folders<Saved>().Keys.Order().ToArray();

    [TestMethod]
    public async Task Start_CoversWhatTheAccountCanEditAndLeavesSubscribedCalendarsOut()
    {
        _http.On(List, Calendars(
                Calendar("mine"), Calendar("team", "writer"), Calendar("shared", "writerWithoutPrivateAccess"),
                Calendar("holidays", "reader"), Calendar("boss", "freeBusyReader")))
            .On(Events("mine"), Json(new { nextSyncToken = "s-mine" }))
            .On(Events("team"), Json(new { nextSyncToken = "s-team" }))
            .On(Events("shared"), Json(new { nextSyncToken = "s-shared" }));

        var cursor = await Source.StartAsync(Account, default);

        CollectionAssert.AreEqual(new[] { "mine", "shared", "team" }, Ids(cursor));
        Assert.AreEqual("s-team", SavedOf(cursor, "team").Sync);
        Assert.AreEqual(0, _http.CountOf(Events("holidays")));
    }

    [TestMethod]
    public async Task Start_FollowsEveryCalendarPage()
    {
        _http.On(List, request => FakeHttp.Json(FakeHttp.Url(request).Contains("pageToken=p2", StringComparison.Ordinal)
                ? Calendars(Calendar("second"))
                : Json(new { nextPageToken = "p2", items = new[] { Calendar("first") } })))
            .On(Events("first"), Json(new { nextSyncToken = "s1" }))
            .On(Events("second"), Json(new { nextSyncToken = "s2" }));

        CollectionAssert.AreEqual(new[] { "first", "second" }, Ids(await Source.StartAsync(Account, default)));
    }

    [TestMethod]
    public async Task Start_WalksToTheLastPageForItsTokenAndReadsNoEvent()
    {
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), request => FakeHttp.Json(FakeHttp.Url(request).Contains("pageToken=p2", StringComparison.Ordinal)
                ? Json(new { nextSyncToken = "final" })
                : Json(new { nextPageToken = "p2" })));

        var cursor = await Source.StartAsync(Account, default);

        Assert.AreEqual("final", SavedOf(cursor, "mine").Sync);
        Assert.IsTrue(_http.Urls.Where(u => u.Contains("/events", StringComparison.Ordinal))
            .All(u => u.Contains("fields=nextPageToken,nextSyncToken", StringComparison.Ordinal)), "an old event is never read back");
    }

    [TestMethod]
    public async Task EveryListing_HoldsTheSameParameters()
    {
        _http.On(List, Calendars(Calendar("mine"))).On(Events("mine"), Json(new { nextSyncToken = "s" }));

        var batch = await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default);
        await Source.StartAsync(Account, default);

        foreach (var url in _http.Urls.Where(u => u.Contains("/events", StringComparison.Ordinal)))
        {
            StringAssert.Contains(url, "singleEvents=true");
            StringAssert.Contains(url, "showDeleted=true");
            StringAssert.Contains(url, $"maxResults={ChangesService.MaxLimit}");
            Assert.IsFalse(url.Contains("timeMin", StringComparison.Ordinal), url);
        }
        Assert.AreEqual(0, batch.Changes.Count);
    }

    [TestMethod]
    public async Task ANewEvent_IsAddedWithItsFacts()
    {
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), Json(new { items = new[] { Item("e1", created: "2026-11-01T11:30:00Z") }, nextSyncToken = "new" }));

        var batch = await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default);

        var change = batch.Changes.Single();
        Assert.AreEqual(ChangeType.Added, change.Type);
        Assert.AreEqual("e1", change.Id);
        Assert.AreEqual("mine", change.CalendarId);
        Assert.AreEqual("capo@example.com", change.From);
        Assert.AreEqual("Riunione", change.Subject);
        Assert.AreEqual("2026-11-02T08:00:00Z", change.Event!.Start, "the same instant whatever zone it was written in");
        Assert.AreEqual("2026-11-02T09:00:00Z", change.Event.End);
        Assert.AreEqual(EventChanges.Confirmed, change.Event.Status);
        Assert.IsFalse(change.Event.OrganizerIsSelf);
        Assert.AreEqual("needsAction", change.Event.MyResponse);
        Assert.AreEqual("new", SavedOf(batch.Next, "mine").Sync);
    }

    [TestMethod]
    public async Task AnOldEventThatChanged_IsUpdated()
    {
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), Json(new { items = new[] { Item("e1") }, nextSyncToken = "new" }));

        var batch = await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default);

        Assert.AreEqual(ChangeType.Updated, batch.Changes.Single().Type);
    }

    [TestMethod]
    public async Task ATentativeEvent_IsConfirmedInTheContractsWords()
    {
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), Json(new { items = new[] { Item("e1", status: "tentative") }, nextSyncToken = "new" }));

        var batch = await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default);

        Assert.AreEqual(EventChanges.Confirmed, batch.Changes.Single().Event!.Status);
    }

    [TestMethod]
    public async Task ADeletedEvent_CarriesItsIdAlone()
    {
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), Json(new { items = new[] { new { id = "e1", status = "cancelled" } }, nextSyncToken = "new" }));

        var change = (await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default)).Changes.Single();

        Assert.AreEqual(ChangeType.Deleted, change.Type);
        Assert.AreEqual("mine", change.CalendarId);
        Assert.IsNull(change.Event);
    }

    [TestMethod]
    public async Task ACancelledInstance_KeepsTheStartItLost()
    {
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), Json(new
            {
                items = new[]
                {
                    new { id = "e1_20261102", status = "cancelled", recurringEventId = "e1", originalStartTime = new { dateTime = "2026-11-02T09:00:00Z" } }
                },
                nextSyncToken = "new"
            }));

        var change = (await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default)).Changes.Single();

        Assert.AreEqual(ChangeType.Deleted, change.Type);
        Assert.AreEqual("2026-11-02T09:00:00Z", change.Event!.Start);
        Assert.IsNull(change.Event.End);
        Assert.AreEqual(EventChanges.Cancelled, change.Event.Status);
    }

    [TestMethod]
    public async Task AnAllDayEvent_KeepsItsDate()
    {
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), Json(new
            {
                items = new[] { new { id = "e1", status = "confirmed", start = new { date = "2026-11-02" }, end = new { date = "2026-11-03" } } },
                nextSyncToken = "new"
            }));

        var change = (await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default)).Changes.Single();

        Assert.AreEqual("2026-11-02", change.Event!.Start);
        Assert.AreEqual("2026-11-03", change.Event.End);
    }

    [TestMethod]
    public async Task ANewDescription_LeavesTheFingerprintUnchanged()
    {
        var before = Item("e1");
        var after = Item("e1");
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), Json(new { items = new[] { before, after }, nextSyncToken = "new" }));

        var batch = await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default);

        Assert.AreEqual(batch.Changes[0].Event!.Fingerprint, batch.Changes[1].Event!.Fingerprint);
    }

    [TestMethod]
    public async Task ATruncatedListing_KeepsItsPageAndTheOldToken()
    {
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), request => FakeHttp.Json(FakeHttp.Url(request).Contains("pageToken=p2", StringComparison.Ordinal)
                ? Json(new { items = new[] { Item("e2") }, nextSyncToken = "new" })
                : Json(new { items = new[] { Item("e1") }, nextPageToken = "p2" })));

        var first = await Source.ReadAsync(Account, Cursor(("mine", "old")), 1, default);

        Assert.AreEqual("e1", first.Changes.Single().Id);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual(new Saved("old", "p2"), SavedOf(first.Next, "mine"));

        var second = await Source.ReadAsync(Account, first.Next, 1, default);

        Assert.AreEqual("e2", second.Changes.Single().Id);
        Assert.IsFalse(second.HasMore);
        Assert.AreEqual(new Saved("new", null), SavedOf(second.Next, "mine"));
        StringAssert.Contains(_http.Urls.Last(), "syncToken=old");
    }

    [TestMethod]
    public async Task ALimitReached_LeavesTheNextCalendarWhereItWas()
    {
        _http.On(List, Calendars(Calendar("a"), Calendar("b")))
            .On(Events("a"), Json(new { items = new[] { Item("e1") }, nextSyncToken = "a-new" }));

        var batch = await Source.ReadAsync(Account, Cursor(("a", "a-old"), ("b", "b-old")), 1, default);

        Assert.IsTrue(batch.HasMore);
        Assert.AreEqual("a-new", SavedOf(batch.Next, "a").Sync);
        Assert.AreEqual("b-old", SavedOf(batch.Next, "b").Sync);
        Assert.AreEqual(0, _http.CountOf(Events("b")));
    }

    [TestMethod]
    public async Task ACalendarGained_BeginsFromNowWithNoEvents()
    {
        _http.On(List, Calendars(Calendar("old"), Calendar("gained")))
            .On(Events("old"), Json(new { nextSyncToken = "old-new" }))
            .On(Events("gained"), Json(new { items = new[] { Item("past") }, nextSyncToken = "g1" }));

        var batch = await Source.ReadAsync(Account, Cursor(("old", "s")), 50, default);

        Assert.AreEqual(0, batch.Changes.Count);
        Assert.AreEqual("g1", SavedOf(batch.Next, "gained").Sync);
    }

    [TestMethod]
    public async Task ACalendarLost_LeavesTheCursor()
    {
        _http.On(List, Calendars(Calendar("kept"))).On(Events("kept"), Json(new { nextSyncToken = "k" }));

        var batch = await Source.ReadAsync(Account, Cursor(("kept", "s"), ("lost", "s")), 50, default);

        CollectionAssert.AreEqual(new[] { "kept" }, Ids(batch.Next));
    }

    [TestMethod]
    public async Task AnExpiredToken_RestartsThatCalendarAndKeepsTheOthers()
    {
        _http.On(List, Calendars(Calendar("fine"), Calendar("expired")))
            .On(Events("fine"), Json(new { items = new[] { Item("e1") }, nextSyncToken = "fine-new" }))
            .On(Events("expired"), request => FakeHttp.Url(request).Contains("syncToken=", StringComparison.Ordinal)
                ? Fail(HttpStatusCode.Gone, "fullSyncRequired")
                : FakeHttp.Json(Json(new { nextSyncToken = "expired-fresh" })));

        var expired = await Assert.ThrowsExactlyAsync<CursorExpiredException>(
            () => Source.ReadAsync(Account, Cursor(("fine", "fine-old"), ("expired", "expired-old")), 50, default));

        Assert.AreEqual("expired-fresh", SavedOf(expired.Replacement!, "expired").Sync);
        Assert.AreEqual("fine-old", SavedOf(expired.Replacement!, "fine").Sync, "its changes are not returned, so it cannot advance");
    }

    [TestMethod]
    public async Task ACreationTimeIsReadFromThePreviousPosition()
    {
        // Created five minutes before the read and after the cursor stood: new, and past the tolerance
        // of a cursor taken an hour ago. The next read stands past it, so it is an update from then on.
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), Json(new { items = new[] { Item("e1", created: EventChanges.Instant(Now.AddMinutes(-5))) }, nextSyncToken = "new" }));

        var first = await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default);
        _clock.Now = Now.AddHours(1);
        var second = await Source.ReadAsync(Account, first.Next, 50, default);
        _clock.Now = Now.AddHours(2);
        var third = await Source.ReadAsync(Account, second.Next, 50, default);

        Assert.AreEqual(ChangeType.Added, first.Changes.Single().Type);
        Assert.AreEqual(ChangeType.Updated, second.Changes.Single().Type);
        Assert.AreEqual(ChangeType.Updated, third.Changes.Single().Type);
    }

    [TestMethod]
    public async Task ACreationWithinTheClockTolerance_StaysNew()
    {
        var justBefore = new { id = "e1", status = "confirmed", created = EventChanges.Instant(Now.AddHours(-1).AddMinutes(-1)), start = new { date = "2026-11-02" }, end = new { date = "2026-11-03" } };
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), Json(new { items = new[] { justBefore }, nextSyncToken = "new" }));

        var batch = await Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default);

        Assert.AreEqual(ChangeType.Added, batch.Changes.Single().Type);
    }

    [TestMethod]
    public async Task AFailedRead_IsAProviderFailure()
    {
        _http.On(List, Calendars(Calendar("mine")))
            .On(Events("mine"), _ => Fail(HttpStatusCode.Forbidden, "rateLimitExceeded"));

        var failure = await Assert.ThrowsExactlyAsync<Google.GoogleApiException>(
            () => Source.ReadAsync(Account, Cursor(("mine", "old")), 50, default));

        Assert.AreEqual("provider_transient", ProviderFailures.Classify(failure, default)!.Error);
    }

    [TestMethod]
    public async Task ACalendarWithNoSyncToken_IsRefusedRatherThanReadWhole()
    {
        _http.On(List, Calendars(Calendar("mine"))).On(Events("mine"), Json(new { items = new[] { Item("old") }, nextSyncToken = "s" }));
        var forged = ChangesCursor.Of("google.calendar", "mine", new { page = (string?)null, since = Now });

        var failure = await Assert.ThrowsExactlyAsync<ChangesException>(() => Source.ReadAsync(Account, forged, 50, default));

        Assert.AreEqual("invalid_cursor", failure.Error);
        Assert.AreEqual(0, _http.CountOf(Events("mine")), "a listing with no token is the whole calendar");
    }
}
