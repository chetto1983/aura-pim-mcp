using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Core.Tools;
using CalendarMcp.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace CalendarMcp.Tests.Tools;

[TestClass]
public sealed class CalendarActionToolTests
{
    [TestMethod]
    public async Task DispatchAction_RoutesEveryPublishedAction()
    {
        using var services = CreateServices();
        var tool = CreateTool(services);

        foreach (var action in CalendarActionTool.ActionNames)
        {
            var args = ArgumentsFor(action);
            var expected = ExpectedOutcome(action);

            if (expected.IsError)
            {
                var error = await Assert.ThrowsExactlyAsync<McpException>(
                    () => tool.DispatchAction(action, args),
                    $"Action '{action}' did not reach its expected validation boundary.");
                Assert.AreEqual(expected.Text, error.Message, $"Action '{action}' reached the wrong dispatch arm.");
            }
            else
            {
                var result = await tool.DispatchAction(action, args);
                StringAssert.Contains(result, expected.Text, $"Action '{action}' reached the wrong dispatch arm.");
            }
        }

        Assert.HasCount(29, CalendarActionTool.ActionNames);
    }

    [TestMethod]
    [DataRow("delete_email", "accountId is required")]
    [DataRow("delete_event", "eventId is required")]
    [DataRow("delete_contact", "accountId is required")]
    [DataRow("unsubscribe_from_email", "accountId is required")]
    [DataRow("bulk_delete_emails", "items array must not be empty")]
    [DataRow("bulk_move_emails", "destination is required")]
    public async Task DispatchAction_DestructiveActionsRejectMissingTargets(string action, string expectedMessage)
    {
        using var services = CreateServices();
        var tool = CreateTool(services);

        var error = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.DispatchAction(action, new CalendarActionArguments()));

        Assert.AreEqual(expectedMessage, error.Message);
    }

    [TestMethod]
    public async Task DispatchAction_UnknownActionListsValidChoices()
    {
        using var services = CreateServices();
        var tool = CreateTool(services);

        var error = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.DispatchAction("delete_everything", new CalendarActionArguments()));

        StringAssert.Contains(error.Message, "Unknown action 'delete_everything'");
        foreach (var action in CalendarActionTool.ActionNames)
        {
            StringAssert.Contains(error.Message, action);
        }
    }

    private static CalendarActionTool CreateTool(IServiceProvider services)
    {
        var registry = services.GetRequiredService<IAccountRegistry>();
        var providerFactory = services.GetRequiredService<IProviderServiceFactory>();
        var attachmentStore = services.GetRequiredService<IAttachmentStore>();
        var tenantContext = services.GetRequiredService<ITenantContext>();
        return new CalendarActionTool(
            registry,
            providerFactory,
            attachmentStore,
            NullLogger<CalendarActionTool>.Instance,
            services,
            tenantContext);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAccountRegistry, EmptyAccountRegistry>();
        services.AddSingleton<IProviderServiceFactory, RejectingProviderFactory>();
        services.AddSingleton<IAttachmentStore, EmptyAttachmentStore>();
        services.AddSingleton<ITenantContext, TenantContext>();
        services.AddSingleton(new UnsubscribeExecutor(
            new TestHttpClientFactory(),
            NullLogger<UnsubscribeExecutor>.Instance));
        return services.BuildServiceProvider();
    }

    private static CalendarActionArguments ArgumentsFor(string action) => action switch
    {
        "get_email_details" or "delete_email" or "get_unsubscribe_info" or "unsubscribe_from_email" =>
            new CalendarActionArguments { AccountId = "account" },
        "mark_email_read" =>
            new CalendarActionArguments { AccountId = "account", EmailId = "email" },
        "move_email" =>
            new CalendarActionArguments { AccountId = "account", EmailId = "email" },
        "get_calendar_event_details" =>
            new CalendarActionArguments { TimeZone = "UTC", EventId = "event" },
        "update_event" =>
            new CalendarActionArguments { AccountId = "account", EventId = "event" },
        "respond_to_event" =>
            new CalendarActionArguments { EventId = "event" },
        "update_contact" or "delete_contact" =>
            new CalendarActionArguments { AccountId = "account" },
        "get_email_attachment" =>
            new CalendarActionArguments { AccountId = "account", EmailId = "email" },
        _ => new CalendarActionArguments(),
    };

    private static Expected ExpectedOutcome(string action) => action switch
    {
        "list_accounts" => Expected.Result("\"accounts\""),
        "get_emails" => Expected.Error("No accounts found"),
        "get_email_details" => Expected.Error("emailId is required"),
        "search_emails" => Expected.Error("query is required"),
        "list_calendars" => Expected.Error("No accounts found"),
        "get_calendar_events" => Expected.Error("timeZone is required"),
        "get_calendar_event_details" => Expected.Error("calendarId is required"),
        "get_contacts" => Expected.Error("No accounts found"),
        "search_contacts" => Expected.Error("query is required"),
        "get_contact_details" => Expected.Error("accountId is required"),
        "create_event" => Expected.Error("subject is required."),
        "update_event" => Expected.Error("calendarId is required"),
        "respond_to_event" => Expected.Error("response is required"),
        "send_email" => Expected.Error("subject is required."),
        "delete_email" => Expected.Error("emailId is required"),
        "mark_email_read" => Expected.Error("mark_email_read requires 'isRead' (true to mark read, false to mark unread)."),
        "move_email" => Expected.Error("destination is required"),
        "delete_event" => Expected.Error("eventId is required"),
        "create_contact" => Expected.Error("displayName is required"),
        "update_contact" => Expected.Error("contactId is required"),
        "delete_contact" => Expected.Error("contactId is required"),
        "get_email_attachment" => Expected.Error("attachmentId is required"),
        "get_contextual_email_summary" => Expected.Error("No accounts configured"),
        "get_guide" => Expected.Result("# Calendar MCP"),
        "get_unsubscribe_info" => Expected.Error("emailId is required"),
        "unsubscribe_from_email" => Expected.Error("emailId is required"),
        "bulk_delete_emails" => Expected.Error("items array must not be empty"),
        "bulk_mark_emails_read" => Expected.Error("items array must not be empty"),
        "bulk_move_emails" => Expected.Error("destination is required"),
        _ => throw new AssertFailedException($"Published action '{action}' has no dispatch expectation."),
    };

    private sealed record Expected(bool IsError, string Text)
    {
        public static Expected Error(string text) => new(true, text);
        public static Expected Result(string text) => new(false, text);
    }

    private sealed class EmptyAccountRegistry : IAccountRegistry
    {
        public Task<IEnumerable<AccountInfo>> GetAllAccountsAsync() =>
            Task.FromResult<IEnumerable<AccountInfo>>([]);

        public Task<AccountInfo?> GetAccountAsync(string accountId) => Task.FromResult<AccountInfo?>(null);
        public IEnumerable<AccountInfo> GetEnabledAccounts() => [];
        public IEnumerable<AccountInfo> GetAccountsByProvider(string provider) => [];
        public IEnumerable<AccountInfo> GetAccountsByDomain(string domain) => [];
    }

    private sealed class RejectingProviderFactory : IProviderServiceFactory
    {
        public IProviderService GetProvider(string accountType) =>
            throw new AssertFailedException("Validation must reject the call before provider resolution.");
    }

    private sealed class EmptyAttachmentStore : IAttachmentStore
    {
        public StoredAttachment? Put(string name, string? contentType, byte[] bytes) => null;
        public StoredAttachment? TryConsume(string attachmentId) => null;
        public StoredAttachment? TryRead(string attachmentId) => null;
        public bool TryDelete(string attachmentId) => false;
        public void EvictExpired() { }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
