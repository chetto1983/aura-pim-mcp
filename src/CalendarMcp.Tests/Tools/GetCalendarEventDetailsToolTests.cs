using System.Text.Json;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tools;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Rocks;

namespace CalendarMcp.Tests.Tools;

[TestClass]
public class GetCalendarEventDetailsToolTests
{
    private const string TestTimeZone = "America/Chicago";
    private static readonly string Reference = EventRef.Encode("acc-1", "ev-1");

    [TestMethod]
    public async Task GetCalendarEventDetails_InvalidTimeZone_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new GetCalendarEventDetailsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetCalendarEventDetailsTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetCalendarEventDetails("Invalid/Zone", "cal-1", Reference));
        Assert.IsTrue(ex.Message.Contains("Invalid IANA timezone"));
    }

    [TestMethod]
    public async Task GetCalendarEventDetails_RejectsAProviderIdPassedAsReference()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new GetCalendarEventDetailsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetCalendarEventDetailsTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetCalendarEventDetails(TestTimeZone, "cal-1", "ev-1"));
        StringAssert.StartsWith(ex.Message, "eventId is not a valid event reference.");
    }

    [TestMethod]
    public async Task GetCalendarEventDetails_EmptyEventId_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new GetCalendarEventDetailsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetCalendarEventDetailsTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetCalendarEventDetails(TestTimeZone, "cal-1", ""));
        Assert.AreEqual("eventId is required", ex.Message);
    }

    [TestMethod]
    public async Task GetCalendarEventDetails_AccountNotFound_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("nonexistent")
            .ReturnValue(Task.FromResult<AccountInfo?>(null));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new GetCalendarEventDetailsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetCalendarEventDetailsTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetCalendarEventDetails(TestTimeZone, "cal-1", EventRef.Encode("nonexistent", "ev-1")));
        Assert.AreEqual("Account 'nonexistent' not found", ex.Message);
        regExp.Verify();
    }

    [TestMethod]
    public async Task GetCalendarEventDetails_EventNotFound_ThrowsMcpException()
    {
        var missing = EventRef.Encode("acc-1", "missing-ev");
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetCalendarEventDetailsAsync("acc-1", "cal-1", "missing-ev", Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<CalendarEvent?>(null));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new GetCalendarEventDetailsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetCalendarEventDetailsTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetCalendarEventDetails(TestTimeZone, "cal-1", missing));
        Assert.IsTrue(ex.Message.Contains("not found"));
        StringAssert.Contains(ex.Message, missing);
        Assert.DoesNotContain("missing-ev", ex.Message);
        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task GetCalendarEventDetails_Success_ReturnsEventJsonWithTimezone()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");
        var evt = TestData.CreateEvent(id: "ev-1", accountId: "acc-1", subject: "Team Standup",
            start: new DateTime(2025, 1, 10, 15, 0, 0, DateTimeKind.Utc),
            end: new DateTime(2025, 1, 10, 16, 0, 0, DateTimeKind.Utc));

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetCalendarEventDetailsAsync("acc-1", "cal-1", "ev-1", Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<CalendarEvent?>(evt));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new GetCalendarEventDetailsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetCalendarEventDetailsTool>.Instance);

        var result = await tool.GetCalendarEventDetails(TestTimeZone, "cal-1", Reference);
        var doc = JsonDocument.Parse(result);

        Assert.AreEqual(Reference, doc.RootElement.GetProperty("eventId").GetString());
        Assert.AreEqual("eventId", doc.RootElement.EnumerateObject().First().Name);
        Assert.IsFalse(doc.RootElement.TryGetProperty("id", out _));
        Assert.IsFalse(doc.RootElement.TryGetProperty("accountId", out _));
        Assert.AreEqual("Team Standup", doc.RootElement.GetProperty("subject").GetString());
        Assert.AreEqual(TestTimeZone, doc.RootElement.GetProperty("timezone").GetString());

        // Verify UTC and local time fields are present
        Assert.IsTrue(doc.RootElement.TryGetProperty("start_utc", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("start_local", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("end_utc", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("end_local", out _));

        // Verify UTC times end with Z
        Assert.IsTrue(doc.RootElement.GetProperty("start_utc").GetString()!.EndsWith("Z"));

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    private static async Task<JsonElement> DetailsOfAnEventWithBodyAsync(string body, string format)
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");
        var plain = TestData.CreateEvent(id: "ev-1", accountId: "acc-1", subject: "Invito");
        var evt = new CalendarEvent
        {
            Id = plain.Id,
            AccountId = plain.AccountId,
            CalendarId = plain.CalendarId,
            Subject = plain.Subject,
            Start = plain.Start,
            End = plain.End,
            Body = body,
            BodyFormat = format
        };

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1").ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetCalendarEventDetailsAsync("acc-1", "cal-1", "ev-1", Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<CalendarEvent?>(evt));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new GetCalendarEventDetailsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetCalendarEventDetailsTool>.Instance);

        return JsonDocument.Parse(await tool.GetCalendarEventDetails(TestTimeZone, "cal-1", Reference)).RootElement.Clone();
    }

    /// <summary>A stranger's invitation lands on the calendar with no one accepting it, so its body is a mail's.</summary>
    [TestMethod]
    public async Task AnInvitationsHiddenText_DoesNotReachTheModel()
    {
        var details = await DetailsOfAnEventWithBodyAsync(
            """<html><body><p>Ci vediamo <a href="https://a.example.com/?utm_source=x">qui</a></p><div style="display:none">SECRET</div></body></html>""",
            "html");

        var body = details.GetProperty("body").GetString()!;

        Assert.AreEqual("markdown", details.GetProperty("bodyFormat").GetString());
        StringAssert.Contains(body, "Ci vediamo");
        StringAssert.Contains(body, "[qui](https://a.example.com/)");
        Assert.IsFalse(body.Contains("SECRET", StringComparison.Ordinal), body);
    }

    [TestMethod]
    public async Task AnInvitationsInvisibleCharacters_DoNotReachTheModel()
    {
        var details = await DetailsOfAnEventWithBodyAsync("Ci vediamo​⁠ qui\U000E0041", "text");

        Assert.AreEqual("text", details.GetProperty("bodyFormat").GetString());
        Assert.AreEqual("Ci vediamo qui", details.GetProperty("body").GetString());
    }

    [TestMethod]
    public async Task AnInvitationTooLargeToConvert_KeepsItsOtherFields()
    {
        var details = await DetailsOfAnEventWithBodyAsync($"<p>{new string('x', 2 * 1024 * 1024)}</p>", "html");

        Assert.AreEqual("omitted", details.GetProperty("bodyFormat").GetString());
        Assert.AreEqual("Invito", details.GetProperty("subject").GetString());
        Assert.IsFalse(details.GetProperty("body").GetString()!.Contains("xxx", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetCalendarEventDetails_AllDayEvent_IsLocalMidnightOnItsOwnDate()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1").ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetCalendarEventDetailsAsync("acc-1", "cal-1", "all-day", Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<CalendarEvent?>(
                TestData.CreateAllDayEvent(new DateOnly(2026, 9, 23), days: 2, id: "all-day", accountId: "acc-1")));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new GetCalendarEventDetailsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetCalendarEventDetailsTool>.Instance);

        var result = await tool.GetCalendarEventDetails(TestTimeZone, "cal-1", EventRef.Encode("acc-1", "all-day"));
        var root = JsonDocument.Parse(result).RootElement;

        Assert.AreEqual("2026-09-23", root.GetProperty("start_date").GetString());
        Assert.AreEqual("2026-09-25", root.GetProperty("end_date").GetString());
        Assert.AreEqual("2026-09-23T00:00:00", root.GetProperty("start_local").GetString());
        Assert.AreEqual("2026-09-25T00:00:00", root.GetProperty("end_local").GetString());
        Assert.AreEqual("2026-09-23T05:00:00Z", root.GetProperty("start_utc").GetString());
        provExp.Verify();
    }

    [TestMethod]
    public void GetCalendarEventDetails_HasNoAccountIdParameter() =>
        EventToolSchema.AssertNoAccountId(typeof(GetCalendarEventDetailsTool), nameof(GetCalendarEventDetailsTool.GetCalendarEventDetails));
}
