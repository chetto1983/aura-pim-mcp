using System.Collections.Concurrent;
using System.Text.Json;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CalendarMcp.Tests.Helpers;

/// <summary>
/// The registered tool surface over two tenants, each with one account on a different provider,
/// so the provider a call resolves says whose account it reached. B's account comes first, so a tool
/// that falls back to "the first account" would pick B's unless the registry scopes it.
/// </summary>
internal static class SurfaceSession
{
    public const string AccountA = "a-account";
    public const string AccountB = "b-account";
    public const string ProviderA = "microsoft365";
    public const string ProviderB = "google";
    public const string DisplayNameB = "B Secret";

    public static Task<InProcessMcpSession> StartAsync(RecordingProviderFactory factory, string tenant = TestData.TenantA)
    {
        var tenantContext = new TenantContext();
        var store = new InMemoryAttachmentStore(
            Options.Create(new AttachmentStoreOptions()), NullLogger<InMemoryAttachmentStore>.Instance, tenantContext);
        return InProcessMcpSession.StartAsync(tenantContext, store, tenant, services =>
        {
            services.Configure<CalendarMcpConfiguration>(c => c.Accounts =
            [
                TestData.CreateAccount(id: AccountB, tenantId: TestData.TenantB, displayName: DisplayNameB, provider: ProviderB),
                TestData.CreateAccount(id: AccountA, tenantId: TestData.TenantA, displayName: "A Account", provider: ProviderA),
            ]);
            services.AddSingleton<IAccountRegistry>(sp => new AccountRegistry(
                sp.GetRequiredService<IOptionsMonitor<CalendarMcpConfiguration>>(),
                NullLogger<AccountRegistry>.Instance,
                tenantContext));
            services.AddSingleton<IProviderServiceFactory>(factory);
            services.AddSingleton<ISendingAddresses>(new FixedSendingAddresses());
            services.AddHttpClient();
            services.AddSingleton<CalendarMcp.Core.Utilities.UnsubscribeExecutor>();
        });
    }

    public static async Task<IList<McpClientTool>> ListToolsAsync()
    {
        await using var session = await StartAsync(new RecordingProviderFactory());
        return await session.Client.ListToolsAsync();
    }

    public static async Task<CallToolResult> CallAsync(RecordingProviderFactory factory, string tool, string argumentsJson)
    {
        await using var session = await StartAsync(factory);
        return await session.Client.CallToolAsync(tool, Arguments(argumentsJson));
    }

    public static Dictionary<string, object?> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value);

    public static string Text(CallToolResult result) =>
        string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
}

/// <summary>Records every provider a tool resolves and wires none of them.</summary>
internal sealed class RecordingProviderFactory : IProviderServiceFactory
{
    private readonly ConcurrentQueue<string> _requested = new();

    public IReadOnlyCollection<string> Requested => _requested.ToArray();

    public IProviderService GetProvider(string accountType)
    {
        _requested.Enqueue(accountType);
        throw new InvalidOperationException($"No '{accountType}' provider is wired in this test.");
    }
}
