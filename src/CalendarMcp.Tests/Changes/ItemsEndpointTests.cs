using System.Net;
using System.Text.Json;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Google;
using CalendarMcp.Core.Changes.Graph;
using CalendarMcp.Core.Models;
using CalendarMcp.Tests.Helpers;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class ItemsEndpointTests
{
    private const string Mine = "mine";

    private static AccountInfo Account(string id, string provider, string tenant = TestData.TenantA, AccountPermissions? permissions = null) =>
        TestData.CreateAccount(id: id, provider: provider, tenantId: tenant, permissions: permissions);

    private static object Ask(string resource, params object[] items) => new { accountId = Mine, resource, items };

    private static ScriptedSource Mail(Func<ItemRef, ItemFacts?> item) =>
        new(ChangesProviders.Google, ChangeResource.Mail) { Item = item };

    [TestMethod]
    public async Task AnItemThatIsGone_IsAnAnswerNotAFailure()
    {
        var mail = Mail(item => item.Id == "here"
            ? new ItemFacts { Id = "here", From = "a@example.com", Subject = "Fattura", OccurredAt = new DateTimeOffset(2026, 10, 31, 15, 47, 0, TimeSpan.FromHours(1)) }
            : null);
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var (status, body) = await pim.PostAsync("/admin/items", Ask("mail", new { id = "here" }, new { id = "deleted" }));

        Assert.AreEqual(200, status);
        Assert.AreEqual("a@example.com", body.GetProperty("items")[0].GetProperty("from").GetString());
        Assert.AreEqual("2026-10-31T14:47:00Z", body.GetProperty("items")[0].GetProperty("occurredAt").GetString());
        Assert.AreEqual("deleted", body.GetProperty("gone")[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task ACalendarItem_ComesBackWithItsCalendar()
    {
        var calendar = new ScriptedSource(ChangesProviders.Google, ChangeResource.Calendar) { Item = _ => null };
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [calendar]);

        var (_, body) = await pim.PostAsync("/admin/items", Ask("calendar", new { id = "e1", calendarId = "work" }));

        Assert.AreEqual("work", body.GetProperty("gone")[0].GetProperty("calendarId").GetString());
    }

    [TestMethod]
    public async Task FiftyItems_AreRead()
    {
        var mail = Mail(item => new ItemFacts { Id = item.Id! });
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);
        var items = Enumerable.Range(0, 50).Select(i => (object)new { id = $"m{i}" }).ToArray();

        var (status, body) = await pim.PostAsync("/admin/items", Ask("mail", items));

        Assert.AreEqual(200, status);
        Assert.AreEqual(50, body.GetProperty("items").GetArrayLength());
    }

    [TestMethod]
    public async Task FiftyOneItems_AreRefused()
    {
        var mail = Mail(_ => throw new AssertFailedException("nothing is read past the limit"));
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);
        var items = Enumerable.Range(0, 51).Select(i => (object)new { id = $"m{i}" }).ToArray();

        var (status, body) = await pim.PostAsync("/admin/items", Ask("mail", items));

        Assert.AreEqual(400, status);
        Assert.AreEqual("too_many_items", body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task AnEventWithNoCalendar_IsRefused()
    {
        var calendar = new ScriptedSource(ChangesProviders.Google, ChangeResource.Calendar) { Item = _ => null };
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [calendar]);

        var (status, body) = await pim.PostAsync("/admin/items", Ask("calendar", new { id = "e1" }));

        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_items", body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task ARequestWithNoItems_IsRefused()
    {
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [Mail(_ => null)]);

        var (status, _) = await pim.PostAsync("/admin/items", new { accountId = Mine, resource = "mail" });

        Assert.AreEqual(400, status);
    }

    [TestMethod]
    public async Task AForeignTenantsAccount_AnswersExactlyLikeOneThatDoesNotExist()
    {
        var read = false;
        var mail = Mail(_ =>
        {
            read = true;
            return null;
        });
        await using var pim = new ChangesPipeline(
            [Account(Mine, "google"), Account("theirs", "google", TestData.TenantB)], [mail]);

        var foreign = await pim.PostAsync("/admin/items", new { accountId = "theirs", resource = "mail", items = new[] { new { id = "m1" } } });
        var missing = await pim.PostAsync("/admin/items", new { accountId = "nobody", resource = "mail", items = new[] { new { id = "m1" } } });

        Assert.AreEqual(404, foreign.Status);
        Assert.AreEqual(missing.Body.GetRawText(), foreign.Body.GetRawText());
        Assert.IsFalse(read);
    }

    [TestMethod]
    [DataRow("ics", "calendar")]
    [DataRow("json", "calendar")]
    [DataRow("caldav", "calendar")]
    public async Task AProviderWithNoSource_NamesItsAnswer(string provider, string resource)
    {
        await using var pim = new ChangesPipeline([Account(Mine, provider)], []);

        var (status, body) = await pim.PostAsync("/admin/items", Ask(resource, new { id = "x", calendarId = "c" }));

        Assert.AreEqual(422, status);
        Assert.AreEqual("unsupported_resource", body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task ALostPermission_IsRefused()
    {
        var denied = AccountPermissions.All.With(AccountPermission.EmailRead, false);
        await using var pim = new ChangesPipeline([Account(Mine, "google", permissions: denied)], [Mail(_ => null)]);

        var (status, _) = await pim.PostAsync("/admin/items", Ask("mail", new { id = "m1" }));

        Assert.AreEqual(403, status);
    }

    [TestMethod]
    public async Task AProviderFailure_FailsTheCallAndDoesNotReadAsGone()
    {
        var mail = Mail(_ => throw new HttpRequestException("reset"));
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var (status, body) = await pim.PostAsync("/admin/items", Ask("mail", new { id = "m1" }));

        Assert.AreEqual(503, status);
        Assert.AreEqual("provider_transient", body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Gmail_ReadsMetadataAndNoBody()
    {
        var http = new FakeHttp().On("/users/me/messages/m1", GmailFixtures.Json(new
        {
            id = "m1",
            internalDate = "1793457600000",
            payload = new { headers = new[] { new { name = "From", value = "Mario Rossi <mario@example.com>" }, new { name = "Subject", value = "Fattura" } } }
        }));

        var facts = await new GoogleMailChanges(GmailFixtures.Provider(http)).ReadItemAsync("a", new ItemRef("m1", null), default);

        Assert.AreEqual("mario@example.com", facts!.From);
        Assert.AreEqual("Mario Rossi", facts.FromName);
        Assert.AreEqual("Fattura", facts.Subject);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1793457600000), facts.OccurredAt);
        StringAssert.Contains(http.Urls.Single(), "format=metadata");
    }

    [TestMethod]
    public async Task Gmail_FallsBackToTheDateHeader()
    {
        var http = new FakeHttp().On("/users/me/messages/m1", GmailFixtures.Json(new
        {
            id = "m1",
            payload = new { headers = new[] { new { name = "Date", value = "Sun, 01 Nov 2026 10:00:00 +0000" } } }
        }));

        var facts = await new GoogleMailChanges(GmailFixtures.Provider(http)).ReadItemAsync("a", new ItemRef("m1", null), default);

        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), facts!.OccurredAt);
    }

    [TestMethod]
    public async Task Gmail_AMailThatIsGone_IsNull()
    {
        var http = new FakeHttp().On("/users/me/messages/m1", GmailFixtures.NotFound, HttpStatusCode.NotFound);

        Assert.IsNull(await new GoogleMailChanges(GmailFixtures.Provider(http)).ReadItemAsync("a", new ItemRef("m1", null), default));
    }

    [TestMethod]
    public async Task GoogleCalendar_ReadsTheEventWithItsFacts()
    {
        var http = new FakeHttp().On("/calendars/work/events/e1", GmailFixtures.Json(new
        {
            id = "e1",
            status = "confirmed",
            summary = "Riunione",
            organizer = new { email = "capo@example.com", self = true },
            start = new { dateTime = "2026-11-02T09:00:00Z" },
            end = new { dateTime = "2026-11-02T10:00:00Z" }
        }));

        var facts = await new GoogleCalendarChanges(GmailFixtures.Provider(http), TimeProvider.System)
            .ReadItemAsync("a", new ItemRef("e1", "work"), default);

        Assert.AreEqual("Riunione", facts!.Subject);
        Assert.AreEqual("work", facts.CalendarId);
        Assert.AreEqual("2026-11-02T09:00:00Z", facts.Event!.Start);
        Assert.IsTrue(facts.Event.OrganizerIsSelf);
    }

    [TestMethod]
    public async Task GoogleCalendar_ACancelledEventWithNothingLeft_IsGone()
    {
        var http = new FakeHttp().On("/calendars/work/events/e1", GmailFixtures.Json(new { id = "e1", status = "cancelled" }));

        Assert.IsNull(await new GoogleCalendarChanges(GmailFixtures.Provider(http), TimeProvider.System)
            .ReadItemAsync("a", new ItemRef("e1", "work"), default));
    }

    [TestMethod]
    public async Task GoogleCalendar_ACancelledEvent_KeepsItsTitle()
    {
        var http = new FakeHttp().On("/calendars/work/events/e1", GmailFixtures.Json(new
        {
            id = "e1",
            status = "cancelled",
            summary = "Riunione",
            organizer = new { email = "capo@example.com" },
            start = new { dateTime = "2026-11-02T09:00:00Z" },
            end = new { dateTime = "2026-11-02T10:00:00Z" }
        }));

        var facts = await new GoogleCalendarChanges(GmailFixtures.Provider(http), TimeProvider.System)
            .ReadItemAsync("a", new ItemRef("e1", "work"), default);

        Assert.AreEqual("Riunione", facts!.Subject);
        Assert.AreEqual("capo@example.com", facts.From);
        Assert.AreEqual(EventChanges.Cancelled, facts.Event!.Status);
    }

    [TestMethod]
    public async Task GraphMail_ReadsOnlyWhatItNeeds()
    {
        var http = new FakeHttp().On("/me/messages/m1", GraphFixtures.Json(new
        {
            id = "m1",
            subject = "Fattura",
            from = new { emailAddress = new { name = "Mario Rossi", address = "mario@example.com" } },
            receivedDateTime = "2026-11-01T11:00:00Z"
        }));

        var facts = await new GraphMailChanges(ChangesProviders.Microsoft365, GraphFixtures.Provider(http), TimeProvider.System)
            .ReadItemAsync("m365", new ItemRef("m1", null), default);

        Assert.AreEqual("mario@example.com", facts!.From);
        Assert.IsFalse(http.Urls.Single().Contains("body", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GraphMail_AMailThatIsGone_IsNull()
    {
        var http = new FakeHttp().On("/me/messages/m1", GraphFixtures.Failure("ErrorItemNotFound"), HttpStatusCode.NotFound);

        Assert.IsNull(await new GraphMailChanges(ChangesProviders.Microsoft365, GraphFixtures.Provider(http), TimeProvider.System)
            .ReadItemAsync("m365", new ItemRef("m1", null), default));
    }

    [TestMethod]
    public async Task GraphCalendar_ReadsTheEventWithItsFacts()
    {
        var http = new FakeHttp().On("/me/calendars/work/events/e1", GraphFixtures.Json(new
        {
            id = "e1",
            subject = "Riunione",
            isOrganizer = true,
            organizer = new { emailAddress = new { address = "capo@example.com" } },
            start = new { dateTime = "2026-11-02T09:00:00.0000000", timeZone = "UTC" },
            end = new { dateTime = "2026-11-02T10:00:00.0000000", timeZone = "UTC" }
        }));

        var facts = await new GraphCalendarChanges(ChangesProviders.Microsoft365, GraphFixtures.Provider(http), TimeProvider.System)
            .ReadItemAsync("m365", new ItemRef("e1", "work"), default);

        Assert.AreEqual("Riunione", facts!.Subject);
        Assert.AreEqual("2026-11-02T09:00:00Z", facts.Event!.Start);
    }

    [TestMethod]
    public async Task GraphCalendar_AnEventThatIsGone_IsNull()
    {
        var http = new FakeHttp().On("/me/calendars/work/events/e1", GraphFixtures.Failure("ErrorItemNotFound"), HttpStatusCode.NotFound);

        Assert.IsNull(await new GraphCalendarChanges(ChangesProviders.Microsoft365, GraphFixtures.Provider(http), TimeProvider.System)
            .ReadItemAsync("m365", new ItemRef("e1", "work"), default));
    }

    [TestMethod]
    public async Task Imap_AnIdFromAChange_ReadsItsMail()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.GreenMail);
        var cursor = await mailbox.StartAsync();
        await mailbox.DeliverAsync(ImapMailbox.Mail("Oggetto"));
        var id = (await mailbox.ReadAsync(cursor)).Changes.Single().Id;

        var facts = await mailbox.Provider.ReadItemAsync(mailbox.Account.Id, new ItemRef(id, null), default);

        Assert.AreEqual("Oggetto", facts!.Subject);
        Assert.AreEqual("mario@example.com", facts.From);
        Assert.AreEqual("Mario Rossi", facts.FromName);
    }

    [TestMethod]
    public async Task Imap_AnExpungedMail_IsGone()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.GreenMail);
        var cursor = await mailbox.StartAsync();
        var uid = await mailbox.DeliverAsync(ImapMailbox.Mail());
        var id = (await mailbox.ReadAsync(cursor)).Changes.Single().Id;

        await mailbox.ExpungeAsync(uid);

        Assert.IsNull(await mailbox.Provider.ReadItemAsync(mailbox.Account.Id, new ItemRef(id, null), default));
    }

    [TestMethod]
    public async Task Imap_AnIdFromAnotherUidSpace_IsGone()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.GreenMail, inbox: "Watched");
        var cursor = await mailbox.StartAsync();
        await mailbox.DeliverAsync(ImapMailbox.Mail());
        var id = (await mailbox.ReadAsync(cursor)).Changes.Single().Id;

        await mailbox.RecreateWatchedFolderAsync();
        await mailbox.DeliverAsync(ImapMailbox.Mail("Un'altra mail con lo stesso UID"));

        Assert.IsNull(await mailbox.Provider.ReadItemAsync(mailbox.Account.Id, new ItemRef(id, null), default));
    }

    [TestMethod]
    public async Task Imap_AnIdFromAFolderThatIsGone_IsGone()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.GreenMail);

        Assert.IsNull(await mailbox.Provider.ReadItemAsync(mailbox.Account.Id, new ItemRef("Nowhere/1/1", null), default));
    }

    [TestMethod]
    public async Task Imap_AMalformedId_IsGoneAndAsksNothing()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.GreenMail);

        Assert.IsNull(await mailbox.Provider.ReadItemAsync(mailbox.Account.Id, new ItemRef("not an imap id", null), default));
    }
}
