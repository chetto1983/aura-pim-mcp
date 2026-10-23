using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Rocks;

namespace CalendarMcp.Tests.Tools;

/// <summary>
/// Upstream's quoted-scalar filter reads each tool's input schema. The curated tool publishes its
/// own multiplexed schema, where every boolean and number is nullable, so these calls go through
/// <c>WithCalendarMcpSurface()</c> to prove the filter is registered there and reads that schema.
/// </summary>
[TestClass]
public sealed class CalendarActionToolStringArgumentTests
{
    [TestMethod]
    public async Task GetEmails_QuotedCountAndFlag_ReachTheProviderAsTheirValues()
    {
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.GetEmailsAsync("acc-1", 5, true, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<IEnumerable<EmailMessage>>([]));

        var result = await CallAsync(provider, new()
        {
            ["action"] = "get_emails",
            ["accountId"] = "acc-1",
            ["count"] = "5",
            ["unreadOnly"] = " TRUE ",
        });

        Assert.AreNotEqual(true, result.IsError, Text(result));
        provider.Verify();
    }

    [TestMethod]
    public async Task MarkEmailRead_QuotedFalse_IsNotTakenAsMissing()
    {
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.MarkEmailAsReadAsync("acc-1", "email-1", false, Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var result = await CallAsync(provider, new()
        {
            ["action"] = "mark_email_read",
            ["accountId"] = "acc-1",
            ["emailId"] = "email-1",
            ["isRead"] = "false",
        });

        Assert.AreNotEqual(true, result.IsError, Text(result));
        provider.Verify();
    }

    [TestMethod]
    public async Task UnparseableQuotedNumber_StillFailsBinding()
    {
        var result = await CallAsync(new IProviderServiceCreateExpectations(), new()
        {
            ["action"] = "get_emails",
            ["accountId"] = "acc-1",
            ["count"] = "fifty",
        });

        Assert.IsTrue(result.IsError, Text(result));
    }

    private static async Task<CallToolResult> CallAsync(
        IProviderServiceCreateExpectations provider, Dictionary<string, object?> arguments)
    {
        var tenantContext = new TenantContext();
        var store = new InMemoryAttachmentStore(
            Options.Create(new AttachmentStoreOptions()), NullLogger<InMemoryAttachmentStore>.Instance, tenantContext);
        var registry = new IAccountRegistryCreateExpectations();
        registry.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(TestData.CreateAccount(id: "acc-1", provider: "microsoft365")));
        var factory = new IProviderServiceFactoryCreateExpectations();
        factory.Setups.GetProvider("microsoft365").ReturnValue(provider.Instance());

        await using var session = await InProcessMcpSession.StartAsync(tenantContext, store, TestData.TenantA, services =>
        {
            services.AddSingleton(registry.Instance());
            services.AddSingleton(factory.Instance());
        });
        return await session.Client.CallToolAsync("calendar", arguments);
    }

    private static string Text(CallToolResult result) =>
        string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
}
