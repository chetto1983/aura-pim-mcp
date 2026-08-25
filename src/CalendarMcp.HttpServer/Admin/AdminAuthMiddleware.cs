using CalendarMcp.Core.Tenancy;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// Validates the admin token for /admin endpoints. The token comes from the
/// CALENDAR_MCP_ADMIN_TOKEN env var (or CalendarMcp:AdminToken config) and is supplied
/// as an Authorization: Bearer or X-Admin-Token header. Aura's cockpit never holds the
/// token — it calls /admin/* through Aura's backend proxy, which injects the header
/// server-side.
///
/// The Blazor admin UI (cookie auth + /admin/ui pages) was removed in the Aura fork, so
/// every /admin route is now a plain token-gated JSON API — including
/// /admin/auth/{id}/google/start, which the cockpit fetches (the proxy supplies the
/// token) to get the Google authorization URL. The only token-exempt route is the
/// Google OAuth redirect callback, which Google invokes directly with the OAuth
/// code/state and no token.
/// </summary>
public class AdminAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _adminToken;
    private readonly ILogger<AdminAuthMiddleware> _logger;

    private static readonly string[] ExemptPaths =
    [
        "/admin/auth/google/callback"
    ];

    public AdminAuthMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<AdminAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _adminToken = CalendarTokenAuthentication.Resolve(configuration);
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        var path = context.Request.Path.Value ?? "";

        // Google's post-consent redirect carries the OAuth code/state, not a token.
        if (IsExemptPath(path))
        {
            await _next(context);
            return;
        }

        // A remote-capable management plane never has an unauthenticated mode.
        if (string.IsNullOrEmpty(_adminToken))
        {
            _logger.LogError("Calendar admin token is not configured; refusing request.");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Calendar admin API is not configured." });
            return;
        }

        // Header-based token auth for every /admin route.
        if (!CalendarTokenAuthentication.IsAuthorized(context.Request, _adminToken))
        {
            _logger.LogWarning("Unauthorized admin API access attempt from {RemoteIp}",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Provide admin token via Authorization: Bearer <token> or X-Admin-Token header." });
            return;
        }

        var identity = context.Request.Headers["X-Aura-Identity"].FirstOrDefault();
        IDisposable binding;
        try
        {
            binding = tenantContext.Bind(identity ?? "");
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("Admin API request carried no valid Aura identity from {RemoteIp}",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Authenticated Aura identity required." });
            return;
        }

        using (binding)
            await _next(context);
    }

    private static bool IsExemptPath(string path)
    {
        foreach (var exempt in ExemptPaths)
        {
            if (path.Equals(exempt, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

}
