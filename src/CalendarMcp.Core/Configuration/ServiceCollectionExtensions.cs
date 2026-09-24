using CalendarMcp.Core.Prompts;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarMcp.Core.Configuration;

/// <summary>
/// Extension methods for configuring Adjutant services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Adjutant core services to the dependency injection container
    /// </summary>
    public static IServiceCollection AddCalendarMcpCore(this IServiceCollection services)
    {
        services.AddSingleton<ITenantContext, TenantContext>();

        // Register authentication services
        services.AddSingleton<IM365AuthenticationService, M365AuthenticationService>();
        services.AddSingleton<IGoogleAuthenticationService, GoogleAuthenticationService>();

        // Register provider services
        services.AddSingleton<IM365ProviderService, M365ProviderService>();
        services.AddSingleton<IGoogleProviderService, GoogleProviderService>();
        services.AddSingleton<IOutlookComProviderService, OutlookComProviderService>();
        services.AddSingleton<IIcsProviderService, IcsProviderService>();
        services.AddSingleton<IJsonCalendarProviderService, JsonCalendarProviderService>();
        services.AddSingleton<IImapProviderService, ImapProviderService>();
        services.AddSingleton<IProviderServiceFactory, ProviderServiceFactory>();

        // DataProtection + PasswordProtector for at-rest encryption of provider passwords
        services.AddCalendarMcpDataProtection();

        // Register HttpClient for ICS provider
        services.AddHttpClient("IcsProvider");

        // Register HttpClient for unsubscribe requests
        services.AddHttpClient("Unsubscribe", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Register unsubscribe executor
        services.AddSingleton<UnsubscribeExecutor>();

        // Register account registry
        services.AddSingleton<IAccountRegistry, AccountRegistry>();

        // Attachment store (in-memory; eviction sweeper is registered by the
        // HTTP server only — stdio mode never uploads, so lazy expiry on
        // consume is sufficient there).
        services.AddOptions<AttachmentStoreOptions>();
        services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();

        // No tool class is registered: WithCalendarActionTool builds the one curated tool, and
        // it constructs each upstream tool class it forwards to through ActivatorUtilities.

        // Register MCP prompts
        services.AddSingleton<CalendarPrompts>();
        services.AddSingleton<EmailPrompts>();
        services.AddSingleton<ContactPrompts>();

        return services;
    }
}
