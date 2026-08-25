using System.Security.Cryptography;
using System.Text;

namespace CalendarMcp.HttpServer.Admin;

internal static class CalendarTokenAuthentication
{
    internal static string? Resolve(IConfiguration configuration) =>
        Environment.GetEnvironmentVariable("CALENDAR_MCP_ADMIN_TOKEN")
        ?? configuration.GetValue<string>("CalendarMcp:AdminToken");

    internal static bool IsAuthorized(HttpRequest request, string expected)
    {
        var authorization = request.Headers.Authorization.FirstOrDefault();
        string? supplied = null;
        if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            supplied = authorization["Bearer ".Length..].Trim();
        supplied ??= request.Headers["X-Admin-Token"].FirstOrDefault();

        if (string.IsNullOrEmpty(supplied))
            return false;
        var left = Encoding.UTF8.GetBytes(supplied);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

/// <summary>
/// Authenticates the remote MCP transport before request metadata is trusted.
/// Tenant binding still happens per tools/call from _meta.aura.user_identifier.
/// </summary>
public sealed class McpServiceAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _serviceToken;
    private readonly ILogger<McpServiceAuthMiddleware> _logger;

    public McpServiceAuthMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<McpServiceAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _serviceToken = CalendarTokenAuthentication.Resolve(configuration);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrEmpty(_serviceToken))
        {
            _logger.LogError("Calendar MCP service token is not configured; refusing request.");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Calendar MCP service is not configured." });
            return;
        }
        if (!CalendarTokenAuthentication.IsAuthorized(context.Request, _serviceToken))
        {
            _logger.LogWarning("Unauthorized Calendar MCP access attempt from {RemoteIp}",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
            return;
        }
        await _next(context);
    }
}
