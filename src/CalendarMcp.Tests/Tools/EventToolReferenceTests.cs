using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tools;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Rocks;

namespace CalendarMcp.Tests.Tools;

/// <summary>
/// What the four reference-taking tools do with the account the reference names: its permission
/// governs, and a provider failure that quotes the provider id quotes the reference instead.
/// </summary>
[TestClass]
public sealed class EventToolReferenceTests
{
    private const string RawId = "raw-ev-42";
    private static readonly string Reference = EventRef.Encode("acc-1", RawId);

    private static AccountInfo Scoped(AccountPermission granted) =>
        TestData.CreateAccount(id: "acc-1", provider: "microsoft365", permissions: AccountPermissions.None.With(granted, true));

    private static IAccountRegistry Registry(AccountInfo account)
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1").ReturnValue(Task.FromResult<AccountInfo?>(account));
        return regExp.Instance();
    }

    private static IProviderServiceFactory Factory(IProviderService provider)
    {
        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provider);
        return factExp.Instance();
    }

    private static IProviderServiceFactory FailingFactory()
    {
        var provExp = new IProviderServiceCreateExpectations();
        var failure = new ProviderOperationException($"Server rejected event {RawId}");
        provExp.Setups.GetCalendarEventDetailsAsync("acc-1", "cal-1", RawId, Arg.Any<CancellationToken>())
            .Callback((_, _, _, _) => throw failure);
        provExp.Setups.UpdateEventAsync("acc-1", "cal-1", RawId, Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Callback((_, _, _, _, _, _, _, _, _, _, _) => throw failure);
        provExp.Setups.DeleteEventAsync("acc-1", "cal-1", RawId, Arg.Any<CancellationToken>())
            .Callback((_, _, _, _) => throw failure);
        provExp.Setups.RespondToEventAsync("acc-1", "cal-1", RawId, "accept", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Callback((_, _, _, _, _, _) => throw failure);
        return Factory(provExp.Instance());
    }

    private static IAccountRegistry Writable() => Registry(TestData.CreateAccount(id: "acc-1", provider: "microsoft365"));

    private static void AssertQuotesOnlyTheReference(McpException error)
    {
        StringAssert.Contains(error.Message, Reference);
        Assert.DoesNotContain(RawId, error.Message);
    }

    [TestMethod]
    public async Task Details_ProviderFailureQuotesTheReference() =>
        AssertQuotesOnlyTheReference(await Assert.ThrowsExactlyAsync<McpException>(() =>
            new GetCalendarEventDetailsTool(Writable(), FailingFactory(), NullLogger<GetCalendarEventDetailsTool>.Instance)
                .GetCalendarEventDetails("UTC", "cal-1", Reference)));

    [TestMethod]
    public async Task Update_ProviderFailureQuotesTheReference() =>
        AssertQuotesOnlyTheReference(await Assert.ThrowsExactlyAsync<McpException>(() =>
            new UpdateEventTool(Writable(), FailingFactory(), NullLogger<UpdateEventTool>.Instance)
                .UpdateEvent("cal-1", Reference)));

    [TestMethod]
    public async Task Delete_ProviderFailureQuotesTheReference() =>
        AssertQuotesOnlyTheReference(await Assert.ThrowsExactlyAsync<McpException>(() =>
            new DeleteEventTool(Writable(), FailingFactory(), NullLogger<DeleteEventTool>.Instance)
                .DeleteEvent(Reference, "cal-1")));

    [TestMethod]
    public async Task Respond_ProviderFailureQuotesTheReference() =>
        AssertQuotesOnlyTheReference(await Assert.ThrowsExactlyAsync<McpException>(() =>
            new RespondToEventTool(Writable(), FailingFactory(), NullLogger<RespondToEventTool>.Instance)
                .RespondToEvent(Reference, "accept", "cal-1")));

    private static IProviderServiceFactory Untouched() => new IProviderServiceFactoryCreateExpectations().Instance();

    [TestMethod]
    public async Task Details_RefusesAReferenceToAnAccountWithoutCalendarRead()
    {
        var error = await Assert.ThrowsExactlyAsync<McpException>(() =>
            new GetCalendarEventDetailsTool(Registry(Scoped(AccountPermission.EmailRead)), Untouched(), NullLogger<GetCalendarEventDetailsTool>.Instance)
                .GetCalendarEventDetails("UTC", "cal-1", Reference));

        StringAssert.Contains(error.Message, "does not permit");
    }

    [TestMethod]
    public async Task Update_RefusesAReferenceToAnAccountWithoutCalendarWrite()
    {
        var error = await Assert.ThrowsExactlyAsync<McpException>(() =>
            new UpdateEventTool(Registry(Scoped(AccountPermission.CalendarRead)), Untouched(), NullLogger<UpdateEventTool>.Instance)
                .UpdateEvent("cal-1", Reference));

        StringAssert.Contains(error.Message, "does not permit modifying calendars");
    }

    [TestMethod]
    public async Task Delete_RefusesAReferenceToAnAccountWithoutCalendarWrite()
    {
        var error = await Assert.ThrowsExactlyAsync<McpException>(() =>
            new DeleteEventTool(Registry(Scoped(AccountPermission.CalendarRead)), Untouched(), NullLogger<DeleteEventTool>.Instance)
                .DeleteEvent(Reference));

        StringAssert.Contains(error.Message, "does not permit modifying calendars");
    }

    [TestMethod]
    public async Task Respond_RefusesAReferenceToAnAccountWithoutCalendarWrite()
    {
        var error = await Assert.ThrowsExactlyAsync<McpException>(() =>
            new RespondToEventTool(Registry(Scoped(AccountPermission.CalendarRead)), Untouched(), NullLogger<RespondToEventTool>.Instance)
                .RespondToEvent(Reference, "accept"));

        StringAssert.Contains(error.Message, "does not permit modifying calendars");
    }
}
