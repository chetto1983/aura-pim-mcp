using System.Security.Claims;
using System.Text.Json;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CalendarMcp.Tests.Tenancy;

/// <summary>
/// The tenant is bound by a call-tool filter, not by any one tool, so a tool that never mentions
/// tenancy still runs inside the caller's. <c>tenant_probe</c> stands for such a tool.
/// </summary>
[TestClass]
public sealed class TenantBindingTests
{
    [TestMethod]
    public async Task EveryToolCallRunsInsideTheCallersTenant()
    {
        var ran = new List<string>();
        await using var session = await StartAsync(TestData.TenantA, null, ran);

        var result = await session.Client.CallToolAsync("tenant_probe");

        Assert.AreNotEqual(true, result.IsError);
        CollectionAssert.AreEqual(new[] { TestData.TenantA }, ran);
    }

    [TestMethod]
    public async Task ListAccounts_SeesOnlyTheCallersAccounts()
    {
        await using var session = await StartAsync(TestData.TenantB, null, []);

        var result = await session.Client.CallToolAsync("list_accounts");

        var ids = JsonDocument.Parse(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text)
            .RootElement.GetProperty("accounts").EnumerateArray()
            .Select(a => a.GetProperty("accountId").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "b-account" }, ids);
    }

    [TestMethod]
    public async Task ACallerWithoutATenantIsRefusedBeforeTheTool()
    {
        var ran = new List<string>();
        await using var session = await StartAsync(
            TestData.TenantA, new ClaimsPrincipal(new ClaimsIdentity()), ran);

        var result = await session.Client.CallToolAsync("tenant_probe");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text, "OAuth subject must be a non-empty UUID");
        Assert.IsEmpty(ran, "The tool must not run for a caller with no tenant.");
    }

    [TestMethod]
    public async Task TheTenantIsUnboundOnceTheCallEnds()
    {
        var tenantContext = new TenantContext();
        await using (var session = await StartAsync(TestData.TenantA, null, [], tenantContext))
            await session.Client.CallToolAsync("tenant_probe");

        Assert.Throws<InvalidOperationException>(() => tenantContext.RequireTenantId());
    }

    private sealed class NoSendingAddress : ISendingAddresses
    {
        public Task<string?> AddressAsync(AccountInfo account, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private static Task<InProcessMcpSession> StartAsync(
        string tenant, ClaimsPrincipal? principal, List<string> ran, TenantContext? tenantContext = null)
    {
        tenantContext ??= new TenantContext();
        var store = new InMemoryAttachmentStore(
            Options.Create(new AttachmentStoreOptions()), NullLogger<InMemoryAttachmentStore>.Instance, tenantContext);
        return InProcessMcpSession.StartAsync(tenantContext, store, tenant, services =>
        {
            services.Configure<CalendarMcpConfiguration>(c => c.Accounts =
            [
                TestData.CreateAccount(id: "a-account", tenantId: TestData.TenantA),
                TestData.CreateAccount(id: "b-account", tenantId: TestData.TenantB),
            ]);
            services.AddSingleton<IAccountRegistry>(sp => new AccountRegistry(
                sp.GetRequiredService<IOptionsMonitor<CalendarMcpConfiguration>>(),
                NullLogger<AccountRegistry>.Instance,
                tenantContext));
            services.AddSingleton<ISendingAddresses, NoSendingAddress>();
            services.AddSingleton(McpServerTool.Create(
                () =>
                {
                    ran.Add(tenantContext.RequireTenantId());
                    return "ok";
                },
                new McpServerToolCreateOptions { Name = "tenant_probe" }));
        }, principal);
    }
}
