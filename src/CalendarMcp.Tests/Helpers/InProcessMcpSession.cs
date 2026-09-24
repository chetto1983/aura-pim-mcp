using System.IO.Pipelines;
using CalendarMcp.Core.Prompts;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Tests.Helpers;

/// <summary>
/// A real MCP client and server over in-memory pipes, registered through the same
/// <c>WithCalendarMcpSurface()</c> extension both hosts call, so the harness registers exactly
/// what they register. The request principal is set by an incoming message filter -- exactly how
/// the stdio server supplies it -- so a test exercises the same tenant path a host does instead
/// of calling the resource method directly.
/// </summary>
internal sealed class InProcessMcpSession : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _stop;
    private readonly Task _running;

    private InProcessMcpSession(ServiceProvider services, McpServer server, CancellationTokenSource stop, Task running, McpClient client)
    {
        _services = services;
        _server = server;
        _stop = stop;
        _running = running;
        Client = client;
    }

    public McpClient Client { get; }

    public static async Task<InProcessMcpSession> StartAsync(
        ITenantContext tenantContext, IAttachmentStore store, string tenant, Action<IServiceCollection>? configure = null)
    {
        var principal = TenantIdentity.LocalPrincipal(tenant);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(tenantContext);
        services.AddSingleton(store);
        // WithCalendarMcpSurface's three WithPrompts<...> calls construct their prompt class
        // through ActivatorUtilities when a prompt is actually invoked, so these registrations
        // are not load-bearing for the tests today -- but AddCalendarMcpCore registers the same
        // three singletons for both hosts, and "registers exactly what both hosts register" means
        // matching that DI graph, not only what today's tests happen to touch.
        services.AddSingleton<CalendarPrompts>();
        services.AddSingleton<EmailPrompts>();
        services.AddSingleton<ContactPrompts>();
        configure?.Invoke(services);
        services.AddMcpServer()
            .WithMessageFilters(filters => filters.AddIncomingFilter(next => (context, cancellationToken) =>
            {
                context.User = principal;
                return next(context, cancellationToken);
            }))
            .WithCalendarMcpSurface();
        var provider = services.BuildServiceProvider();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "test", NullLoggerFactory.Instance),
            provider.GetRequiredService<IOptions<McpServerOptions>>().Value,
            NullLoggerFactory.Instance,
            provider);
        var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance));
        return new InProcessMcpSession(provider, server, stop, running, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
        }
        await _server.DisposeAsync();
        await _services.DisposeAsync();
        _stop.Dispose();
    }
}
