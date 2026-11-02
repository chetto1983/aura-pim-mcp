using System.Collections.Concurrent;
using CalendarMcp.Core.Services;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// M365 authentication service using MSAL with per-account token caching
/// </summary>
public class M365AuthenticationService : IM365AuthenticationService
{
    private readonly ILogger<M365AuthenticationService> _logger;
    private readonly ConcurrentDictionary<string, AccountApp> _apps = new();

    private sealed record AccountApp(string TenantId, string ClientId, IPublicClientApplication App);

    public M365AuthenticationService(ILogger<M365AuthenticationService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string> AuthenticateInteractiveAsync(
        string tenantId,
        string clientId,
        string[] scopes,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var app = await GetOrCreateAppAsync(tenantId, clientId, accountId);

        try
        {
            _logger.LogInformation("Starting interactive authentication for account {AccountId}...", accountId);
            _logger.LogInformation("A browser window will open for you to sign in.");

            var authResult = await app.AcquireTokenInteractive(scopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cancellationToken);

            _logger.LogInformation("✓ Interactive authentication successful for account {AccountId}", accountId);
            return authResult.AccessToken;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Authentication cancelled for account {AccountId}", accountId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication failed for account {AccountId}: {Message}", accountId, ex.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<string> AuthenticateWithDeviceCodeAsync(
        string tenantId,
        string clientId,
        string[] scopes,
        string accountId,
        Func<string, Task> deviceCodeCallback,
        CancellationToken cancellationToken = default)
    {
        var app = await GetOrCreateAppAsync(tenantId, clientId, accountId);

        try
        {
            _logger.LogInformation("Starting Device Code authentication for account {AccountId}...", accountId);

            var authResult = await app.AcquireTokenWithDeviceCode(scopes, deviceCodeResult =>
            {
                // Call the callback with the device code message for the user to display
                return deviceCodeCallback(deviceCodeResult.Message);
            })
            .ExecuteAsync(cancellationToken);

            _logger.LogInformation("✓ Device Code authentication successful for account {AccountId}", accountId);
            return authResult.AccessToken;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Device Code authentication cancelled for account {AccountId}", accountId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device Code authentication failed for account {AccountId}: {Message}", accountId, ex.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetTokenSilentlyAsync(
        string tenantId,
        string clientId,
        string[] scopes,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var app = await GetOrCreateAppAsync(tenantId, clientId, accountId);

        return await NullWhenGrantIsGoneAsync(async () =>
        {
            var accounts = await app.GetAccountsAsync();
            if (!accounts.Any())
            {
                _logger.LogWarning("No cached account found for {AccountId}. Interactive authentication required.", accountId);
                return null;
            }

            _logger.LogDebug("Attempting silent authentication for account {AccountId}...", accountId);
            var result = await app.AcquireTokenSilent(scopes, accounts.First())
                .ExecuteAsync(cancellationToken);

            _logger.LogDebug("✓ Silent authentication successful for account {AccountId}", accountId);
            return result.AccessToken;
        }, accountId);
    }

    /// <inheritdoc/>
    public async Task<bool> HasSignedInUserAsync(string tenantId, string clientId, string accountId)
    {
        try
        {
            var app = await GetOrCreateAppAsync(tenantId, clientId, accountId);
            return (await app.GetAccountsAsync()).Any();
        }
        catch (Exception ex)
        {
            // One account's bad client must not cost the operator the whole list of accounts.
            _logger.LogWarning(ex, "Could not read the sign-in state of account {AccountId}", accountId);
            return false;
        }
    }

    // Only this exception says the grant is gone. Null reads as "reconnect the account", so a
    // network blip during a refresh must not end here: it would pause a person's watch.
    internal async Task<string?> NullWhenGrantIsGoneAsync(Func<Task<string?>> acquire, string accountId)
    {
        try
        {
            return await acquire();
        }
        catch (MsalUiRequiredException)
        {
            _logger.LogWarning("Silent authentication failed for account {AccountId}. Interactive authentication required.", accountId);
            return null;
        }
    }

    /// <summary>
    /// Get or create an IPublicClientApplication for a specific account. An account can be removed
    /// and added again under the same id with another tenant or client, so the id alone does not
    /// identify the application.
    /// </summary>
    internal async Task<IPublicClientApplication> GetOrCreateAppAsync(
        string tenantId,
        string clientId,
        string accountId)
    {
        if (_apps.TryGetValue(accountId, out var existing)
            && existing.TenantId == tenantId
            && existing.ClientId == clientId)
        {
            return existing.App;
        }

        // Create cache file name unique to this account
        var cacheFileName = $"msal_cache_{accountId}.bin";
        var cacheDirectory = Configuration.ConfigurationPaths.GetDataDirectory();

        // Ensure directory exists
        Directory.CreateDirectory(cacheDirectory);

        // Build MSAL application
        // Use the default redirect URI for desktop/native apps which works for both 
        // organizational (M365) and consumer (Outlook.com) accounts
        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithDefaultRedirectUri()
            .Build();

        // Set up token cache persistence
        var storagePropertiesBuilder = new StorageCreationPropertiesBuilder(
            cacheFileName,
            cacheDirectory);

        // On Linux (containers), use plaintext file storage since there's
        // no desktop keyring available in headless environments
        if (OperatingSystem.IsLinux())
        {
            storagePropertiesBuilder.WithLinuxUnprotectedFile();
        }
        else if (OperatingSystem.IsMacOS())
        {
            storagePropertiesBuilder.WithMacKeyChain(
                serviceName: "CalendarMcp.MsalCache",
                accountName: cacheFileName);
        }

        var storageProperties = storagePropertiesBuilder.Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
        cacheHelper.RegisterCache(app.UserTokenCache);

        _logger.LogDebug("Initialized MSAL app for account {AccountId} with cache file {CacheFile}",
            accountId, cacheFileName);

        _apps[accountId] = new AccountApp(tenantId, clientId, app);
        return app;
    }
}
