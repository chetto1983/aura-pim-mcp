using System.Text.Json.Nodes;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Core.Tools;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Rocks;

namespace CalendarMcp.Tests.Tools;

/// <summary>
/// The event actions address an event by the EventRef get_calendar_events and create_event
/// mint: the account and provider id reach upstream decoded, and only the reference comes back.
/// </summary>
[TestClass]
public sealed class CalendarActionToolEventRefTests
{
    private const string AccountId = "acc-1";
    private const string RawEventId = "raw-event-1";
    private static readonly string Reference = EventRef.Encode(AccountId, RawEventId);

    [TestMethod]
    public async Task CreateEvent_ReturnsAReferenceToTheCreatedEvent()
    {
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.CreateEventAsync(
            AccountId, Arg.Any<string?>(), "Standup", Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(RawEventId));

        var result = await Dispatch(provider, "create_event", new CalendarActionArguments
        {
            Subject = "Standup",
            Start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc),
            AccountId = AccountId,
        });

        Assert.AreEqual(Reference, JsonNode.Parse(result)!["eventId"]!.GetValue<string>());
        provider.Verify();
    }

    [TestMethod]
    public async Task UpdateEvent_ForwardsTheDecodedEventAndEchoesTheReference()
    {
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.UpdateEventAsync(
            AccountId, "primary", RawEventId, "Renamed", Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var result = await Dispatch(provider, "update_event", new CalendarActionArguments
        {
            EventId = Reference,
            CalendarId = "primary",
            Subject = "Renamed",
        });

        AssertEchoesOnlyTheReference(result);
        provider.Verify();
    }

    [TestMethod]
    public async Task DeleteEvent_ForwardsTheDecodedEventAndEchoesTheReference()
    {
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.DeleteEventAsync(AccountId, "primary", RawEventId, Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var result = await Dispatch(provider, "delete_event", new CalendarActionArguments { EventId = Reference });

        AssertEchoesOnlyTheReference(result);
        provider.Verify();
    }

    [TestMethod]
    public async Task RespondToEvent_ForwardsTheDecodedEventAndEchoesTheReference()
    {
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.RespondToEventAsync(
            AccountId, "primary", RawEventId, "accept", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var result = await Dispatch(provider, "respond_to_event", new CalendarActionArguments
        {
            EventId = Reference,
            Response = "accept",
        });

        AssertEchoesOnlyTheReference(result);
        provider.Verify();
    }

    [TestMethod]
    public async Task EventDetails_NotFoundNamesTheReferenceNotTheProviderId()
    {
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.GetCalendarEventDetailsAsync(AccountId, "primary", RawEventId, Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<CalendarEvent?>(null));

        var error = await Assert.ThrowsExactlyAsync<McpException>(() => Dispatch(provider, "get_calendar_event_details",
            new CalendarActionArguments { TimeZone = "UTC", CalendarId = "primary", EventId = Reference }));

        StringAssert.Contains(error.Message, Reference);
        Assert.DoesNotContain(RawEventId, error.Message);
        provider.Verify();
    }

    [TestMethod]
    [DataRow("get_calendar_event_details")]
    [DataRow("update_event")]
    [DataRow("delete_event")]
    [DataRow("respond_to_event")]
    public async Task EventActions_RejectAProviderIdPassedAsReference(string action)
    {
        var error = await Assert.ThrowsExactlyAsync<McpException>(() => Dispatch(
            new IProviderServiceCreateExpectations(), action, new CalendarActionArguments
            {
                EventId = RawEventId,
                TimeZone = "UTC",
                CalendarId = "primary",
                Response = "accept",
            }));

        StringAssert.StartsWith(error.Message, "eventId is not a valid event reference.");
    }

    [TestMethod]
    [DataRow("mark_email_read")]
    [DataRow("bulk_mark_emails_read")]
    public async Task MarkRead_RequiresAnExplicitIsRead(string action)
    {
        var error = await Assert.ThrowsExactlyAsync<McpException>(() => Dispatch(
            new IProviderServiceCreateExpectations(), action, new CalendarActionArguments
            {
                AccountId = AccountId,
                EmailId = "email-1",
                Items = [new BulkEmailItem { AccountId = AccountId, EmailId = "email-1" }],
            }));

        Assert.AreEqual($"{action} requires 'isRead' (true to mark read, false to mark unread).", error.Message);
    }

    private static void AssertEchoesOnlyTheReference(string result)
    {
        Assert.AreEqual(Reference, JsonNode.Parse(result)!["eventId"]!.GetValue<string>());
        Assert.DoesNotContain(RawEventId, result);
    }

    private static async Task<string> Dispatch(
        IProviderServiceCreateExpectations provider, string action, CalendarActionArguments args)
    {
        // Not verified: the rejection cases never reach the account or the provider.
        var registry = new IAccountRegistryCreateExpectations();
        registry.Setups.GetAccountAsync(AccountId)
            .ReturnValue(Task.FromResult<AccountInfo?>(TestData.CreateAccount(id: AccountId, provider: "microsoft365")));
        var factory = new IProviderServiceFactoryCreateExpectations();
        factory.Setups.GetProvider("microsoft365").ReturnValue(provider.Instance());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(registry.Instance());
        services.AddSingleton(factory.Instance());
        services.AddSingleton<ITenantContext, TenantContext>();
        await using var serviceProvider = services.BuildServiceProvider();
        var tool = new CalendarActionTool(serviceProvider, serviceProvider.GetRequiredService<ITenantContext>());
        return await tool.DispatchAction(action, args);
    }
}
