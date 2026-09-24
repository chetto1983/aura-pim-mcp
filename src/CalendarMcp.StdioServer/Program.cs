using System.Security.Claims;
using CalendarMcp.Core.Apps;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Events;

namespace CalendarMcp.StdioServer;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Use shared configuration paths (ensures consistency with CLI and token storage)
        var configDir = ConfigurationPaths.GetDataDirectory();
        var logDir = ConfigurationPaths.GetLogDirectory();
        var configPath = ConfigurationPaths.GetConfigFilePath();
        
        // Ensure directories exist
        ConfigurationPaths.EnsureDataDirectoryExists();
        
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        
        // If no OTLP endpoint, use Serilog for file logging as fallback
        if (string.IsNullOrEmpty(otlpEndpoint))
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    path: Path.Combine(logDir, "calendar-mcp-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        
        Log.Information("Adjutant Server starting. Config directory: {ConfigDir}", configDir);

        // stdio carries no bearer: the process serves the one tenant the CLI added accounts for.
        // stderr, not the log: with OTLP configured the Serilog logger above does not exist.
        ClaimsPrincipal tenant;
        try
        {
            tenant = TenantIdentity.LocalPrincipal(Environment.GetEnvironmentVariable(TenantIdentity.LocalTenantVariable));
        }
        catch (ArgumentException)
        {
            await Console.Error.WriteLineAsync(
                $"{TenantIdentity.LocalTenantVariable} must name the tenant UUID the accounts were added for with the CLI.");
            return 1;
        }

        try
        {
            var builder = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    // Clear default configuration sources
                    config.Sources.Clear();
                    
                    // Add configuration from the user data directory (primary)
                    if (File.Exists(configPath))
                    {
                        config.AddJsonFile(configPath, optional: false, reloadOnChange: true);
                        Log.Information("Loaded configuration from {ConfigPath}", configPath);
                    }
                    else
                    {
                        // Fallback: try application directory (for development)
                        var appDir = AppContext.BaseDirectory;
                        var appConfigPath = Path.Combine(appDir, "appsettings.json");
                        if (File.Exists(appConfigPath))
                        {
                            config.AddJsonFile(appConfigPath, optional: false, reloadOnChange: true);
                            Log.Information("Loaded configuration from application directory: {ConfigPath}", appConfigPath);
                        }
                        else
                        {
                            Log.Warning("No appsettings.json found. Expected at: {UserConfigPath} or {AppConfigPath}", 
                                configPath, appConfigPath);
                        }
                    }
                    
                    // Add environment variables (can override file settings)
                    config.AddEnvironmentVariables("CALENDAR_MCP_");
                    
                    // Add command line args
                    config.AddCommandLine(args);
                });

            if (!string.IsNullOrEmpty(otlpEndpoint))
            {
                // Use OpenTelemetry if OTLP endpoint is configured
                builder.ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddOpenTelemetry(options =>
                    {
                        options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                            .AddService("calendar-mcp-stdio"));
                        
                        options.AddOtlpExporter();
                        options.IncludeFormattedMessage = true;
                        options.IncludeScopes = true;
                    });
                });
            }
            else
            {
                // Use Serilog for file logging if no OTLP endpoint
                builder.UseSerilog();
            }

            builder.ConfigureServices((context, services) =>
            {
                // Configure Adjutant settings
                services.Configure<CalendarMcpConfiguration>(
                    context.Configuration.GetSection("CalendarMcp"));
                
                // Add Adjutant core services (providers, tools, account registry)
                services.AddCalendarMcpCore();
                
                // The same surface as the HttpServer: upstream's 29 tools as ONE curated,
                // action-multiplexed tool (D-17..D-26), which binds its tenant from the request's
                // principal -- set here, for every message, to the local tenant.
                services.AddMcpServer(CalendarMcpServerOptions.Configure)
                    .WithMessageFilters(filters => filters.AddIncomingFilter(next => (context, cancellationToken) =>
                    {
                        context.User = tenant;
                        return next(context, cancellationToken);
                    }))
                    .WithCalendarActionTool()
                    // The MCP Apps view (ui://calendar/view.html). The tool's own _meta.ui is
                    // set in WithCalendarActionTool's factory, beside the schema patch.
                    .WithCalendarView()
                    // attachment://stash/{id}: the file behind get_email_attachment's resource_link.
                    .WithEmailAttachmentResource()
                    .WithPrompts<CalendarMcp.Core.Prompts.CalendarPrompts>()
                    .WithPrompts<CalendarMcp.Core.Prompts.EmailPrompts>()
                    .WithPrompts<CalendarMcp.Core.Prompts.ContactPrompts>()
                    .WithStdioServerTransport();
            });

            var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
        finally
        {
            if (string.IsNullOrEmpty(otlpEndpoint))
            {
                await Log.CloseAndFlushAsync();
            }
        }
    }
}
