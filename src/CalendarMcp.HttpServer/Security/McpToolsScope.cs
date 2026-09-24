using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace CalendarMcp.HttpServer.Security;

/// <summary>
/// The one test for "this bearer may use the tools": its space-separated <c>scope</c> claim names
/// the configured tools scope (<c>OAuth:ToolsScope</c>). The MCP endpoint applies it as an
/// authorization policy and <c>AdminAuthMiddleware</c> inline, so a token valid for this
/// audience but issued for another purpose reaches neither the tools nor the admin API.
/// </summary>
internal static class McpToolsScope
{
    internal const string PolicyName = "McpTools";

    internal static bool IsGranted(ClaimsPrincipal user, string toolsScope) =>
        user.FindFirst("scope")?.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(toolsScope, StringComparer.Ordinal) == true;

    internal static AuthorizationBuilder AddMcpToolsPolicy(this AuthorizationBuilder authorization, string toolsScope) =>
        authorization.AddPolicy(PolicyName, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => IsGranted(context.User, toolsScope)));
}
