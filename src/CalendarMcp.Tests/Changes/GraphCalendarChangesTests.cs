using System.Net;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Graph;
using CalendarMcp.Core.Models;
using CalendarMcp.Tests.Helpers;
using static CalendarMcp.Tests.Changes.GraphFixtures;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class GraphCalendarChangesTests
{
    private const string Account = "m365";
    private const string Kind = "microsoft365.calendar";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeHttp _http = new();
    private readonly FixedClock _clock = new(Now);

    private GraphCalendarChanges Source => new(ChangesProviders.Microsoft365, Provider(_http), _clock);

    private static string Delta(string calendar) => $"/me/calendars/{calendar}/calendarView/delta";

    private static string Link(string calendar, string token) => $"{Base}{Delta(calendar)}?$deltatoken={token}";

    private FakeHttp Calendars(params (string Id, bool CanEdit)[] calendars) => _http.On(
        request => request.RequestUri!.AbsolutePath.EndsWith("/me/calendars", StringComparison.Ordinal),
        _ => FakeHttp.Json(Json(new { value = calendars.Select(c => new { id = c.Id, canEdit = c.CanEdit }) })));

    private static object Item(
        string id, string created = "2026-10-01T09:00:00Z", bool cancelled = false, bool isOrganizer = false,
        string response = "notResponded", string start = "2026-11-02T08:00:00.0000000", string end = "2026-11-02T09:00:00.0000000",
        bool allDay = false) => new
        {
            id,
            subject = "Riunione",
            createdDateTime = created,
            lastModifiedDateTime = "2026-11-01T11:59:00Z",
            isCancelled = cancelled,
            isOrganizer,
            isAllDay = allDay,
            organizer = new { emailAddress = new { address = "capo@example.com" } },
            responseStatus = new { response },
            start = new { dateTime = start, timeZone = "UTC" },
            end = new { dateTime = end, timeZone = "UTC" }
        };

    private sealed record Saved(string Link, Saved? Next, DateTimeOffset Since, DateTimeOffset WindowEnd, bool RoundDone);

    private static ChangesCursor Cursor(params (string Id, string Link)[] calendars) => ChangesCursor.Of(
        Kind, calendars.Select(c => new KeyValuePair<string, Saved>(c.Id, new Saved(c.Link, null, Now.AddHours(-1), Now.AddDays(399), true))));

    private static Saved SavedOf(ChangesCursor cursor, string calendar) =>
        ChangesCursor.Decode(cursor.Encode(), Kind).Folder<Saved>(calendar);

    private static string[] Ids(ChangesCursor cursor) =>
        ChangesCursor.Decode(cursor.Encode(), Kind).Folders<Saved>().Keys.Order().ToArray();

    [TestMethod]
    public async Task Start_CoversWhatTheAccountCanEditAndLeavesReadOnlyCalendarsOut()
    {
        Calendars(("mine", true), ("team", true), ("holidays", false));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "m1")))
            .On(Delta("team"), GraphFixtures.Delta(Link("team", "t1")));

        var cursor = await Source.StartAsync(Account, default);

        CollectionAssert.AreEqual(new[] { "mine", "team" }, Ids(cursor));
        Assert.AreEqual(Link("team", "t1"), SavedOf(cursor, "team").Link);
        Assert.AreEqual(0, _http.CountOf(Delta("holidays")));
    }

    [TestMethod]
    public async Task Start_FixesAWindowAndThrowsItsEventsAway()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), Next(Link("mine", "next").Replace("deltatoken", "skiptoken"), Item("old")))
            .On("skiptoken=next", GraphFixtures.Delta(Link("mine", "m1"), Item("older")));

        var cursor = await Source.StartAsync(Account, default);

        var first = _http.Urls.First(url => url.Contains("calendarView", StringComparison.Ordinal));
        StringAssert.Contains(first, "startDateTime=2026-10-31T12:00:00Z");
        StringAssert.Contains(first, "endDateTime=2027-12-06T12:00:00Z");
        Assert.AreEqual(Link("mine", "m1"), SavedOf(cursor, "mine").Link);
    }

    [TestMethod]
    public async Task ANewEvent_IsAddedWithItsFacts()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"), Item("e1", created: "2026-11-01T11:30:00Z")));

        var batch = await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default);

        var change = batch.Changes.Single();
        Assert.AreEqual(ChangeType.Added, change.Type);
        Assert.AreEqual("mine", change.CalendarId);
        Assert.AreEqual("capo@example.com", change.From);
        Assert.AreEqual("Riunione", change.Subject);
        Assert.AreEqual("2026-11-02T08:00:00Z", change.Event!.Start);
        Assert.AreEqual("2026-11-02T09:00:00Z", change.Event.End);
        Assert.AreEqual(EventChanges.Confirmed, change.Event.Status);
        Assert.AreEqual("needsAction", change.Event.MyResponse);
        Assert.IsFalse(change.Event.OrganizerIsSelf);
        Assert.AreEqual(Link("mine", "new"), SavedOf(batch.Next, "mine").Link);
    }

    [TestMethod]
    public async Task AnOldEventThatChanged_IsUpdated()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"), Item("e1")));

        var batch = await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default);

        Assert.AreEqual(ChangeType.Updated, batch.Changes.Single().Type);
    }

    [TestMethod]
    public async Task ACancelledMeeting_StaysAnUpdateWhoseStatusSaysSo()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"), Item("e1", cancelled: true)));

        var change = (await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default)).Changes.Single();

        Assert.AreEqual(ChangeType.Updated, change.Type);
        Assert.AreEqual(EventChanges.Cancelled, change.Event!.Status);
    }

    [TestMethod]
    [DataRow("accepted", "accepted")]
    [DataRow("organizer", "accepted")]
    [DataRow("declined", "declined")]
    [DataRow("tentativelyAccepted", "tentative")]
    [DataRow("notResponded", "needsAction")]
    [DataRow("none", "needsAction")]
    public async Task AResponse_IsSaidInTheContractsWords(string graph, string contract)
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"), Item("e1", response: graph, isOrganizer: graph == "organizer")));

        var change = (await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default)).Changes.Single();

        Assert.AreEqual(contract, change.Event!.MyResponse);
        Assert.AreEqual(graph == "organizer", change.Event.OrganizerIsSelf);
    }

    [TestMethod]
    public async Task AnAllDayEvent_KeepsItsDate()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"),
            Item("e1", allDay: true, start: "2026-11-02T00:00:00.0000000", end: "2026-11-03T00:00:00.0000000")));

        var change = (await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default)).Changes.Single();

        Assert.AreEqual("2026-11-02", change.Event!.Start);
        Assert.AreEqual("2026-11-03", change.Event.End);
    }

    [TestMethod]
    public async Task ARemovedRowThatIsGone_IsDeleted()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"), Removed("e1")))
            .OnError("/me/calendars/mine/events/e1", HttpStatusCode.NotFound, "ErrorItemNotFound");

        var change = (await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default)).Changes.Single();

        Assert.AreEqual(ChangeType.Deleted, change.Type);
        Assert.AreEqual("mine", change.CalendarId);
        Assert.IsNull(change.Event);
    }

    [TestMethod]
    public async Task ARemovedRowThatStillExists_IsAnEventOutsideTheWindowNotACancellation()
    {
        // The reference: @removed also marks events outside the range that were added or changed.
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"), Removed("e1")))
            .On("/me/calendars/mine/events/e1", Json(Item("e1", start: "2029-01-01T08:00:00.0000000", end: "2029-01-01T09:00:00.0000000")));

        var change = (await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default)).Changes.Single();

        Assert.AreEqual(ChangeType.Updated, change.Type);
        Assert.AreEqual("2029-01-01T08:00:00Z", change.Event!.Start);
    }

    [TestMethod]
    public async Task ARowWithNoStart_IsReadInFull()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"), Row("e1")))
            .On("/me/calendars/mine/events/e1", Json(Item("e1")));

        var change = (await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default)).Changes.Single();

        Assert.AreEqual("2026-11-02T08:00:00Z", change.Event!.Start);
        Assert.IsTrue(_http.Requests.Last().Headers.GetValues("Prefer").Contains("outlook.timezone=\"UTC\""));
    }

    [TestMethod]
    public async Task TheSameEventTwiceInARound_ComesOnceWithTheLatestRow()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"),
            Item("e1", start: "2026-11-02T08:00:00.0000000"), Item("e1", start: "2026-11-03T08:00:00.0000000")));

        var change = (await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default)).Changes.Single();

        Assert.AreEqual("2026-11-03T08:00:00Z", change.Event!.Start);
    }

    [TestMethod]
    public async Task EveryRequest_AsksForUtcSoATimeNeedsNoZone()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new")));

        await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 7, default);

        var read = _http.Requests.Single(r => FakeHttp.Url(r).Contains("calendarView", StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(
            new[] { "odata.maxpagesize=7", "outlook.timezone=\"UTC\"" },
            read.Headers.GetValues("Prefer").ToArray());
    }

    [TestMethod]
    public async Task ADeletedEvent_ReadsNotFoundBecauseItsIdDoesNotLast()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"), Removed("e1")))
            .OnError("/me/calendars/mine/events/e1", HttpStatusCode.NotFound, "ErrorItemNotFound");

        var change = (await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default)).Changes.Single();

        Assert.AreEqual(ChangeType.Deleted, change.Type);
        Assert.IsTrue(_http.Requests.Where(r => FakeHttp.Url(r).Contains("/events/", StringComparison.Ordinal))
            .All(r => !r.Headers.GetValues("Prefer").Contains(LastingIds)));
    }

    [TestMethod]
    public async Task ATruncatedRound_KeepsItsNextLinkAndNeverSkips()
    {
        var page2 = Link("mine", "x").Replace("$deltatoken=x", "$skiptoken=page2");
        Calendars(("mine", true));
        _http.On("deltatoken=old", Next(page2, Item("e1")))
            .On("skiptoken=page2", GraphFixtures.Delta(Link("mine", "new"), Item("e2")));

        var first = await Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 1, default);

        Assert.AreEqual("e1", first.Changes.Single().Id);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual(page2, SavedOf(first.Next, "mine").Link);

        var second = await Source.ReadAsync(Account, first.Next, 1, default);

        Assert.AreEqual("e2", second.Changes.Single().Id);
        Assert.IsFalse(second.HasMore);
        Assert.AreEqual(Link("mine", "new"), SavedOf(second.Next, "mine").Link);
    }

    [TestMethod]
    public async Task ACalendarLeftForLater_KeepsThePageHonest()
    {
        Calendars(("a", true), ("b", true));
        _http.On(Delta("a"), GraphFixtures.Delta(Link("a", "new"), Item("e1")));

        var batch = await Source.ReadAsync(Account, Cursor(("a", Link("a", "old")), ("b", Link("b", "old"))), 1, default);

        Assert.IsTrue(batch.HasMore);
        Assert.AreEqual(Link("b", "old"), SavedOf(batch.Next, "b").Link);
        Assert.AreEqual(0, _http.CountOf(Delta("b")));
    }

    [TestMethod]
    public async Task ACalendarGained_BeginsFromNowWithNoEvents()
    {
        Calendars(("old", true), ("gained", true));
        _http.On(Delta("old"), GraphFixtures.Delta(Link("old", "new")))
            .On(Delta("gained"), GraphFixtures.Delta(Link("gained", "g1"), Item("past")));

        var batch = await Source.ReadAsync(Account, Cursor(("old", Link("old", "old"))), 50, default);

        Assert.AreEqual(0, batch.Changes.Count);
        Assert.AreEqual(Link("gained", "g1"), SavedOf(batch.Next, "gained").Link);
    }

    [TestMethod]
    public async Task ACalendarLost_LeavesTheCursor()
    {
        Calendars(("kept", true));
        _http.On(Delta("kept"), GraphFixtures.Delta(Link("kept", "new")));

        var batch = await Source.ReadAsync(Account, Cursor(("kept", Link("kept", "old")), ("lost", Link("lost", "old"))), 50, default);

        CollectionAssert.AreEqual(new[] { "kept" }, Ids(batch.Next));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Gone, "resyncRequired")]
    [DataRow(HttpStatusCode.BadRequest, "syncStateNotFound")]
    public async Task AnExpiredLink_RestartsThatCalendarAndKeepsTheOthers(HttpStatusCode status, string code)
    {
        Calendars(("fine", true), ("expired", true));
        _http.On(Delta("fine"), GraphFixtures.Delta(Link("fine", "new"), Item("e1")))
            .On(Delta("expired"), GraphFixtures.Delta(Link("expired", "fresh")))
            .OnError("deltatoken=stale", status, code);

        var expired = await Assert.ThrowsExactlyAsync<CursorExpiredException>(() => Source.ReadAsync(
            Account, Cursor(("fine", Link("fine", "old")), ("expired", Link("expired", "stale"))), 50, default));

        Assert.AreEqual(Link("expired", "fresh"), SavedOf(expired.Replacement!, "expired").Link);
        Assert.AreEqual(Link("fine", "old"), SavedOf(expired.Replacement!, "fine").Link, "its changes are not returned, so it cannot advance");
    }

    [TestMethod]
    public async Task AWindowRunningShort_IsReplacedWithNoGap()
    {
        var aging = ChangesCursor.Of(Kind, "mine", new Saved(Link("mine", "old"), null, Now.AddHours(-1), Now.AddDays(100), true));
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "fresh"), Item("in-the-new-window-first-round")))
            .On("deltatoken=old", GraphFixtures.Delta(Link("mine", "new"), Item("e1")));

        var batch = await Source.ReadAsync(Account, aging, 50, default);

        Assert.AreEqual("e1", batch.Changes.Single().Id, "the old window reports what the new one's first round threw away");
        Assert.AreEqual(Link("mine", "fresh"), SavedOf(batch.Next, "mine").Link);
        Assert.AreEqual(Now.AddDays(400), SavedOf(batch.Next, "mine").WindowEnd);
    }

    [TestMethod]
    [DataRow("https://evil.example.com/steal")]
    [DataRow("http://graph.microsoft.com/v1.0/me/calendars/mine/calendarView/delta")]
    [DataRow("https://graph.microsoft.com.evil.example.com/v1.0/delta")]
    public async Task AForgedLink_NeverGetsARequest(string link)
    {
        var failure = await Assert.ThrowsExactlyAsync<ChangesException>(
            () => Source.ReadAsync(Account, Cursor(("mine", link)), 50, default));

        Assert.AreEqual("invalid_cursor", failure.Error);
        Assert.AreEqual(0, _http.Requests.Count, "the account's bearer token goes on every request");
    }

    [TestMethod]
    public async Task AFailedEventRead_FailsThePage()
    {
        Calendars(("mine", true));
        _http.On(Delta("mine"), GraphFixtures.Delta(Link("mine", "new"), Removed("e1")))
            .OnError("/me/calendars/mine/events/e1", HttpStatusCode.ServiceUnavailable, "serviceNotAvailable");

        var failure = await Assert.ThrowsAsync<Exception>(
            () => Source.ReadAsync(Account, Cursor(("mine", Link("mine", "old"))), 50, default));

        Assert.AreEqual("provider_transient", ProviderFailures.Classify(failure, default)!.Error);
    }
}
