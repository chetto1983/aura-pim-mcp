using CalendarMcp.Core.Models;

namespace CalendarMcp.Core.Configuration;

/// <summary>
/// Root configuration for Adjutant
/// </summary>
public class CalendarMcpConfiguration
{
    /// <summary>
    /// List of configured accounts
    /// </summary>
    public List<AccountInfo> Accounts { get; set; } = new();
    
    /// <summary>
    /// Telemetry configuration
    /// </summary>
    public TelemetryConfiguration Telemetry { get; set; } = new();

    /// <summary>
    /// Base URL a browser uses to reach this server (e.g. "https://calendar-mcp.tail920062.ts.net"),
    /// used to build the Google callback the relay forwards to. A <c>returnBase</c> passed to
    /// the start endpoint wins over it; when both are absent, request headers decide.
    /// The environment-variable form is CALENDAR_MCP_CalendarMcp__ExternalBaseUrl: configuration
    /// is loaded with AddEnvironmentVariables("CALENDAR_MCP_"), so the prefix is stripped and the
    /// remainder still has to name the CalendarMcp section.
    /// </summary>
    public string? ExternalBaseUrl { get; set; }

    /// <summary>
    /// The redirect URI registered in the Google OAuth client. It is a shared relay page that
    /// forwards the browser to the callback named in <c>state</c>, so one fixed URI serves every
    /// install whatever address it is reached by — a LAN IP included, which Google rejects as a
    /// redirect URI of its own.
    /// </summary>
    public string GoogleOAuthRelayUrl { get; set; } = "https://chetto1983.github.io/aura-connect/google/callback/";
}

/// <summary>
/// Telemetry configuration
/// </summary>
public class TelemetryConfiguration
{
    /// <summary>
    /// Whether telemetry is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// OTLP endpoint for OpenTelemetry export (if specified)
    /// </summary>
    public string? OtlpEndpoint { get; set; }
    
    /// <summary>
    /// Minimum log level
    /// </summary>
    public string MinimumLevel { get; set; } = "Information";
}
