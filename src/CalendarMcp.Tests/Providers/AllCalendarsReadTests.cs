using System.Net;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Rocks;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// An omitted calendarId means every calendar, and "primary" means the default one. Before this,
/// Google and Graph answered an omitted id from the default calendar alone, and a tool that says
/// "omit for all calendars" reported a day as empty while a second calendar held the meeting.
/// </summary>
[TestClass]
public class AllCalendarsReadTests
{
    private static readonly DateTime Start = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private const string GoogleCalendars = """
        {"items":[
          {"id":"me@example.com","summary":"Mine","accessRole":"owner","primary":true},
          {"id":"team@group.example.com","summary":"Team","accessRole":"writer"}
        ]}
        """;

    private static string GoogleEvents(string id, string start) => $$$"""
        {"items":[{"id":"{{{id}}}","summary":"{{{id}}}","start":{"dateTime":"{{{start}}}"},"end":{"dateTime":"{{{start}}}"}}]}
        """;

    private static GoogleProviderService Google(FakeHttp http) =>
        new(NullLogger<GoogleProviderService>.Instance, new IAccountRegistryCreateExpectations().Instance())
        {
            Transport = new FakeGoogleTransport(http)
        };

    private const string GraphCalendars = """
        {"value":[
          {"id":"cal-default","name":"Calendar","canEdit":true,"isDefaultCalendar":true},
          {"id":"cal-team","name":"Team","canEdit":true,"isDefaultCalendar":false}
        ]}
        """;

    private static string GraphEvents(string id, string start) => $$$"""
        {"value":[{"id":"{{{id}}}","subject":"{{{id}}}","start":{"dateTime":"{{{start}}}","timeZone":"UTC"},"end":{"dateTime":"{{{start}}}","timeZone":"UTC"}}]}
        """;

    private static IEnumerable<IProviderService> GraphProviders(FakeHttp http)
    {
        var account = TestData.CreateAccount(id: "a", providerConfig: new() { ["tenantId"] = "t", ["clientId"] = "c" });
        var registry = new SingleAccountRegistry(account);
        var auth = new StaticTokenAuthentication();

        yield return new M365ProviderService(NullLogger<M365ProviderService>.Instance, auth, registry) { Transport = http };
        yield return new OutlookComProviderService(NullLogger<OutlookComProviderService>.Instance, auth, registry) { Transport = http };
    }

    [TestMethod]
    public async Task Google_OmittedId_ReadsEveryCalendarInStartOrder()
    {
        var http = new FakeHttp()
            .On("/users/me/calendarList", GoogleCalendars)
            .On("/calendars/primary/events", GoogleEvents("late", "2026-11-02T15:00:00Z"))
            .On("/calendars/team@group.example.com/events", GoogleEvents("early", "2026-11-02T09:00:00Z"));

        var events = (await Google(http).GetCalendarEventsAsync("a", null, Start, End)).ToList();

        CollectionAssert.AreEqual(new[] { "early", "late" }, events.Select(e => e.Id).ToList());
        CollectionAssert.AreEqual(new[] { "team@group.example.com", "primary" }, events.Select(e => e.CalendarId).ToList());
    }

    [TestMethod]
    public async Task Google_Primary_ReadsOnlyTheDefaultCalendar()
    {
        var http = new FakeHttp().On("/calendars/primary/events", GoogleEvents("mine", "2026-11-02T15:00:00Z"));

        var events = (await Google(http).GetCalendarEventsAsync("a", "primary", Start, End)).ToList();

        Assert.AreEqual("mine", events.Single().Id);
        Assert.AreEqual(0, http.CountOf("/calendarList"), "the alias names one calendar and needs no listing");
    }

    [TestMethod]
    public async Task Google_CalendarListOnASecondPage_IsStillRead()
    {
        var http = new FakeHttp()
            .On("/users/me/calendarList", """{"items":[{"id":"me@example.com","primary":true}],"nextPageToken":"p2"}""")
            .On("pageToken=p2", """{"items":[{"id":"second@example.com"}]}""")
            .On("/calendars/primary/events", """{"items":[]}""")
            .On("/calendars/second@example.com/events", GoogleEvents("found", "2026-11-02T09:00:00Z"));

        var events = await Google(http).GetCalendarEventsAsync("a", null, Start, End);

        Assert.AreEqual("found", events.Single().Id);
    }

    [TestMethod]
    public async Task Google_NoCalendarListed_StillReadsTheDefault()
    {
        var http = new FakeHttp()
            .On("/users/me/calendarList", """{"items":[]}""")
            .On("/calendars/primary/events", GoogleEvents("mine", "2026-11-02T09:00:00Z"));

        var events = await Google(http).GetCalendarEventsAsync("a", null, Start, End);

        Assert.AreEqual("mine", events.Single().Id);
    }

    [TestMethod]
    public async Task Google_OneUnreadableCalendar_KeepsTheRestAndSaysSo()
    {
        var http = new FakeHttp()
            .On("/users/me/calendarList", GoogleCalendars)
            .On("/calendars/primary/events", GoogleEvents("mine", "2026-11-02T09:00:00Z"))
            .On("/calendars/team@group.example.com/events", """{"error":{"code":403,"message":"no"}}""", HttpStatusCode.Forbidden);

        var events = await Google(http).GetCalendarEventsAsync("a", null, Start, End);

        Assert.AreEqual("mine", events.Single().Id);
        var partial = events as PartialCalendarRead;
        Assert.IsNotNull(partial, "a calendar that could not be read must not vanish silently");
        Assert.AreEqual("Team", partial.Unread.Single().CalendarName);
    }

    [TestMethod]
    public async Task Google_EveryCalendarUnreadable_IsAnErrorNotAnEmptyDay()
    {
        var http = new FakeHttp()
            .On("/users/me/calendarList", GoogleCalendars)
            .On("/calendars/", """{"error":{"code":500,"message":"down"}}""", HttpStatusCode.InternalServerError)
            .On("/users/me/calendarList", GoogleCalendars);

        await Assert.ThrowsExactlyAsync<Google.GoogleApiException>(
            () => Google(http).GetCalendarEventsAsync("a", null, Start, End));
    }

    [TestMethod]
    public async Task Graph_OmittedId_ReadsEveryCalendarAndKeepsThePrimaryAlias()
    {
        var http = new FakeHttp()
            .On("/me/calendars", GraphCalendars)
            .On("/me/calendar/calendarView", GraphEvents("late", "2026-11-02T15:00:00.0000000"))
            .On("/me/calendars/cal-team/calendarView", GraphEvents("early", "2026-11-02T09:00:00.0000000"));

        foreach (var provider in GraphProviders(http))
        {
            var events = (await provider.GetCalendarEventsAsync("a", null, Start, End)).ToList();

            CollectionAssert.AreEqual(new[] { "early", "late" }, events.Select(e => e.Id).ToList());
            CollectionAssert.AreEqual(new[] { "cal-team", "primary" }, events.Select(e => e.CalendarId).ToList());
        }
    }

    [TestMethod]
    public async Task Graph_Primary_ReadsOnlyTheDefaultCalendar()
    {
        var http = new FakeHttp().On("/me/calendar/calendarView", GraphEvents("mine", "2026-11-02T15:00:00.0000000"));

        foreach (var provider in GraphProviders(http))
        {
            var events = await provider.GetCalendarEventsAsync("a", "primary", Start, End);

            Assert.AreEqual("mine", events.Single().Id);
        }
        Assert.AreEqual(0, http.CountOf("/me/calendars"));
    }

    [TestMethod]
    public async Task Graph_CalendarsOnASecondPage_AreStillListed()
    {
        var http = new FakeHttp()
            .On("/me/calendars", """
                {"value":[{"id":"one","isDefaultCalendar":true}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/calendars?$skip=10"}
                """)
            .On("$skip=10", """{"value":[{"id":"two"}]}""");

        foreach (var provider in GraphProviders(http))
        {
            var calendars = await provider.ListCalendarsAsync("a");

            CollectionAssert.AreEqual(new[] { "one", "two" }, calendars.Select(c => c.Id).ToList());
        }
    }

    [TestMethod]
    public async Task CountCapsTheMergedResult()
    {
        var http = new FakeHttp()
            .On("/users/me/calendarList", GoogleCalendars)
            .On("/calendars/primary/events", GoogleEvents("late", "2026-11-02T15:00:00Z"))
            .On("/calendars/team@group.example.com/events", GoogleEvents("early", "2026-11-02T09:00:00Z"));

        var events = await Google(http).GetCalendarEventsAsync("a", null, Start, End, count: 1);

        Assert.AreEqual("early", events.Single().Id);
    }
}
