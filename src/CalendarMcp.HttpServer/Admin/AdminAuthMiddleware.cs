using CalendarMcp.Core.Tenancy;
using CalendarMcp.HttpServer.Security;

namespace CalendarMcp.HttpServer.Admin;

public sealed class AdminAuthMiddleware(
    RequestDelegate next,
    string toolsScope)
{
    private static readonly string[] ExemptPaths = ["/admin/auth/google/callback"];

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        var path = context.Request.Path.Value ?? "";
        if (ExemptPaths.Any(exempt => path.Equals(exempt, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "OAuth bearer token required." });
            return;
        }
        if (!McpToolsScope.IsGranted(context.User, toolsScope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = $"OAuth token lacks {toolsScope} scope." });
            return;
        }

        string tenantId;
        try
        {
            tenantId = TenantIdentity.FromPrincipal(context.User);
        }
        catch (ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Authenticated OAuth subject required." });
            return;
        }

        using (tenantContext.Bind(tenantId))
            await next(context);
    }
}
