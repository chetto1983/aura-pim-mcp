using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Google;
using CalendarMcp.Core.Changes.Graph;
using CalendarMcp.Core.Discovery;
using CalendarMcp.Core.Prompts;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Providers.Dav;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Core.Utilities;
using DnsClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        services.AddSingleton<HostGuard>();
        services.AddSingleton<IImapAccountVerifier, ImapAccountVerifier>();
        services.AddSingleton<DavHttpClient>();
        services.AddSingleton<DavDiscovery>();
        services.AddSingleton<IDavProviderService, DavProviderService>();
        services.AddSingleton<IProviderServiceFactory, ProviderServiceFactory>();
        services.AddSingleton<ISendingAddresses, SendingAddresses>();
        services.AddSingleton<ChangesService>();
        services.AddSingleton<IChangesSource>(provider =>
            new GoogleMailChanges(provider.GetRequiredService<IGoogleProviderService>()));
        services.AddSingleton<IChangesSource>(provider =>
            new GoogleCalendarChanges(provider.GetRequiredService<IGoogleProviderService>(), TimeProvider.System));
        services.AddSingleton<IChangesSource>(provider => new GraphMailChanges(
            ChangesProviders.Microsoft365, provider.GetRequiredService<IM365ProviderService>(), TimeProvider.System));
        services.AddSingleton<IChangesSource>(provider => new GraphMailChanges(
            ChangesProviders.Outlook, provider.GetRequiredService<IOutlookComProviderService>(), TimeProvider.System));
        services.AddSingleton<IChangesSource>(provider => new GraphCalendarChanges(
            ChangesProviders.Microsoft365, provider.GetRequiredService<IM365ProviderService>(), TimeProvider.System));
        services.AddSingleton<IChangesSource>(provider => new GraphCalendarChanges(
            ChangesProviders.Outlook, provider.GetRequiredService<IOutlookComProviderService>(), TimeProvider.System));
        services.AddSingleton<IChangesSource>(provider => provider.GetRequiredService<IImapProviderService>());

        // DataProtection + PasswordProtector for at-rest encryption of provider passwords
        services.AddCalendarMcpDataProtection();

        // Register HttpClient for ICS provider
        services.AddHttpClient("IcsProvider");

        // DAV client follows redirects manually inside DavHttpClient. The handler is pooled
        // across accounts, so cookies are off: a session cookie set for one account must not
        // ride along with another account's Basic credentials.
        services.AddHttpClient(DavHttpClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });

        // Mail discovery from an address (Discovery/MailDiscovery): MX through DnsClient, autoconfig through a
        // guarded client that follows no redirect; the autoconfig XML is cached, at most SizeLimit entries.
        services.AddMemoryCache(options => options.SizeLimit = 1000);
        services.AddSingleton<ILookupClient>(_ => new LookupClient(new LookupClientOptions
        {
            Timeout = TimeSpan.FromSeconds(2),
            Retries = 1
        }));
        services.AddSingleton<IMxLookup, MxLookup>();
        services.AddHttpClient(AutoconfigClient.HttpClientName, client => client.Timeout = AutoconfigClient.Timeout)
            .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<HostGuard>().CreatePublicHttpHandler());
        services.AddSingleton<IAutoconfigClient>(provider => new AutoconfigClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(AutoconfigClient.HttpClientName),
            provider.GetRequiredService<ILogger<AutoconfigClient>>()));
        services.AddSingleton(provider => new MailDiscovery(
            provider.GetRequiredService<IAutoconfigClient>(),
            provider.GetRequiredService<IMxLookup>(),
            provider.GetRequiredService<IMemoryCache>(),
            TimeProvider.System,
            DiscoveryBudget.Default,
            provider.GetRequiredService<ILogger<MailDiscovery>>()));

        // A one-click unsubscribe URL is a mail sender's: guarded, no private address, no redirect followed.
        services.AddHttpClient(UnsubscribeExecutor.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<HostGuard>().CreatePublicHttpHandler());

        // Register unsubscribe executor
        services.AddSingleton<UnsubscribeExecutor>();

        // Register account registry
        services.AddSingleton<IAccountRegistry, AccountRegistry>();

        // Attachment store (in-memory; eviction sweeper is registered by the HTTP
        // server only -- stdio mode has no sweeper, but every get_email_attachment
        // read now stashes there too, so memory stays bounded by MaxTotalBytes
        // rather than by an upload-only sweeper).
        services.AddOptions<AttachmentStoreOptions>();
        services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();

        // Register MCP prompts
        services.AddSingleton<CalendarPrompts>();
        services.AddSingleton<EmailPrompts>();
        services.AddSingleton<ContactPrompts>();

        services.AddHostedService<ConversionThreadLogging>();

        return services;
    }
}
