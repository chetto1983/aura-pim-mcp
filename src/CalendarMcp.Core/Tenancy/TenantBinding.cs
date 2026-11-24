using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tenancy;

/// <summary>
/// Binds the caller's tenant around every tool call, so each tool, present and future, runs inside
/// the tenant of the request principal and none has to remember to bind it. A caller with no valid
/// tenant is refused before the tool is reached.
/// </summary>
public static class TenantBinding
{
    public static IMcpServerBuilder WithTenantBinding(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(Filter));

    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            IDisposable scope;
            try
            {
                scope = context.Services!.GetRequiredService<ITenantContext>()
                    .Bind(TenantIdentity.FromPrincipal(context.User));
            }
            catch (ArgumentException ex)
            {
                throw new McpException(ex.Message);
            }
            using (scope)
                return await next(context, cancellationToken);
        };
}
