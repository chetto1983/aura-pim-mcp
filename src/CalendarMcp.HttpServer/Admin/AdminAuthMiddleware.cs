using CalendarMcp.Core.Tenancy;

namespace CalendarMcp.HttpServer.Admin;

public sealed class AdminAuthMiddleware(
    RequestDelegate next)
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
        if (!HasScope(context.User.FindFirst("scope")?.Value, "mcp:tools"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "OAuth token lacks mcp:tools scope." });
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

    private static bool HasScope(string? raw, string required) =>
        raw?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(required, StringComparer.Ordinal) == true;
}
