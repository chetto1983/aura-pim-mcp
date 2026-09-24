using CalendarMcp.Auth;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// Maps admin API endpoints for account management and device code authentication.
/// </summary>
public static class AdminEndpoints
{
    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin");

        // Account management (read)
        admin.MapGet("/accounts", ListAccounts);
        admin.MapGet("/accounts/{accountId}/status", GetAccountStatus);

        // Account CRUD
        admin.MapPost("/accounts", CreateAccount);
        admin.MapPut("/accounts/{accountId}", UpdateAccount);
        admin.MapDelete("/accounts/{accountId}", DeleteAccount);
        admin.MapPost("/accounts/{accountId}/logout", LogoutAccount);

        // Device code authentication
        admin.MapPost("/auth/{accountId}/start", StartDeviceCodeAuth);
        admin.MapGet("/auth/{accountId}/status", GetAuthStatus);
        admin.MapPost("/auth/{accountId}/cancel", CancelAuth);

        // Google OAuth redirect flow
        admin.MapGet("/auth/{accountId}/google/start", StartGoogleOAuth);
        admin.MapGet("/auth/google/callback", GoogleOAuthCallback);

        return app;
    }

    /// <summary>
    /// List all configured accounts with their basic info (no secrets).
    /// </summary>
    private static async Task<IResult> ListAccounts(IAccountRegistry accountRegistry)
    {
        var accounts = await accountRegistry.GetAllAccountsAsync();
        var response = accounts.Select(a => new
        {
            id = a.Id,
            displayName = a.DisplayName,
            provider = a.Provider,
            domains = a.Domains,
            enabled = a.Enabled,
            priority = a.Priority,
            permissions = DescribePermissions(a)
        });

        return Results.Ok(new { accounts = response });
    }

    /// <summary>
    /// Projects an account's granted and effective permissions. "granted" is what the operator
    /// set; "effective" is that intersected with what the provider actually supports, so a
    /// client can tell a revoked grant apart from one the provider can never honour.
    /// </summary>
    private static object DescribePermissions(AccountInfo account)
    {
        var effective = AccountCapabilities.GetEffectivePermissions(account);
        return new
        {
            granted = ToDictionary(account.Permissions),
            effective = ToDictionary(effective)
        };

        static Dictionary<string, bool> ToDictionary(AccountPermissions permissions) =>
            AccountPermissions.AllPermissions.ToDictionary(
                AccountPermissions.ToPropertyName,
                permissions.IsGranted);
    }

    /// <summary>
    /// Get authentication status for a specific account.
    /// </summary>
    private static async Task<IResult> GetAccountStatus(
        string accountId,
        IAccountRegistry accountRegistry,
        DeviceCodeAuthManager authManager)
    {
        var account = await accountRegistry.GetAccountAsync(accountId);
        if (account == null)
        {
            return Results.NotFound(new { error = $"Account '{accountId}' not found." });
        }

        var flowStatus = authManager.GetFlowStatus(accountId);

        return Results.Ok(new
        {
            accountId = account.Id,
            displayName = account.DisplayName,
            provider = account.Provider,
            enabled = account.Enabled,
            linked = GoogleLinked(account),
            permissions = DescribePermissions(account),
            authFlow = flowStatus.Status != "not_found" ? flowStatus : null
        });
    }

    /// <summary>
    /// Whether a Google account holds the token its OAuth exchange stored; null for other
    /// providers, whose link state is either reported through authFlow (device code) or does not
    /// exist (credential and feed providers). A file check, so polling it never calls Google.
    /// </summary>
    internal static bool? GoogleLinked(AccountInfo account) =>
        string.Equals(account.Provider, "google", StringComparison.OrdinalIgnoreCase)
            ? File.Exists(ConfigurationPaths.GetGoogleTokenFilePath(account.Id))
            : null;

    /// <summary>
    /// Create a new account in the config file.
    /// </summary>
    private static async Task<IResult> CreateAccount(
        CreateAccountRequest request,
        IAccountConfigurationService configService,
        ITenantContext tenantContext,
        IConfiguration configuration)
    {
        // Validate ID
        var (idValid, idError) = AccountValidation.ValidateAccountId(request.Id);
        if (!idValid)
            return Results.BadRequest(new { error = idError });

        // Validate provider
        var (provValid, provError) = AccountValidation.ValidateProvider(request.Provider);
        if (!provValid)
            return Results.BadRequest(new { error = provError });

        // Validate provider config
        var (cfgValid, cfgError) = AccountValidation.ValidateProviderConfig(request.Provider, request.ProviderConfig);
        if (!cfgValid)
            return Results.BadRequest(new { error = cfgError });

        var account = new AccountInfo
        {
            Id = TenantIdentity.AccountId(tenantContext.RequireTenantId(), request.Id),
            TenantId = tenantContext.RequireTenantId(),
            DisplayName = request.DisplayName,
            Provider = request.Provider,
            Domains = request.Domains,
            Enabled = request.Enabled,
            Priority = request.Priority,
            Permissions = request.Permissions ?? AccountPermissions.All,
            ProviderConfig = request.ProviderConfig
        };

        try
        {
            await configService.AddAccountAsync(account);
            AccountConfigurationReload.Apply(configuration);
            return Results.Created($"/admin/accounts/{account.Id}/status", new
            {
                id = account.Id,
                displayName = account.DisplayName,
                provider = account.Provider,
                domains = account.Domains,
                enabled = account.Enabled,
                priority = account.Priority,
                permissions = DescribePermissions(account)
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing account in the config file.
    /// </summary>
    private static async Task<IResult> UpdateAccount(
        string accountId,
        UpdateAccountRequest request,
        IAccountConfigurationService configService,
        IConfiguration configuration)
    {
        // Look up existing account to get its provider (provider is immutable)
        var existing = await configService.GetAccountFromConfigAsync(accountId);
        if (existing is null)
            return Results.NotFound(new { error = $"Account '{accountId}' not found." });

        // Validate provider config against the existing provider
        var (cfgValid, cfgError) = AccountValidation.ValidateProviderConfig(existing.Provider, request.ProviderConfig);
        if (!cfgValid)
            return Results.BadRequest(new { error = cfgError });

        var updated = new AccountInfo
        {
            Id = accountId,
            TenantId = existing.TenantId,
            DisplayName = request.DisplayName,
            Provider = existing.Provider, // immutable
            Domains = request.Domains,
            Enabled = request.Enabled,
            Priority = request.Priority,
            Permissions = request.Permissions ?? existing.Permissions,
            ProviderConfig = request.ProviderConfig
        };

        try
        {
            await configService.UpdateAccountAsync(updated);
            AccountConfigurationReload.Apply(configuration);
            return Results.Ok(new
            {
                id = updated.Id,
                displayName = updated.DisplayName,
                provider = updated.Provider,
                domains = updated.Domains,
                enabled = updated.Enabled,
                priority = updated.Priority,
                permissions = DescribePermissions(updated)
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Remove an account from the config file. Optionally clear credentials.
    /// </summary>
    private static async Task<IResult> DeleteAccount(
        string accountId,
        IAccountConfigurationService configService,
        IConfiguration configuration,
        bool logout = false)
    {
        try
        {
            await configService.RemoveAccountAsync(accountId, clearCredentials: logout);
            AccountConfigurationReload.Apply(configuration);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Clear cached credentials for an account without removing it from config.
    /// </summary>
    private static async Task<IResult> LogoutAccount(
        string accountId,
        IAccountConfigurationService configService)
    {
        var account = await configService.GetAccountFromConfigAsync(accountId);
        if (account is null)
            return Results.NotFound(new { error = $"Account '{accountId}' not found." });

        await configService.ClearCredentialsAsync(accountId, account.Provider);
        return Results.Ok(new { message = $"Credentials cleared for account '{accountId}'." });
    }

    /// <summary>
    /// Start a device code authentication flow for the specified account.
    /// Returns the device code and verification URL for the user to complete authentication.
    /// </summary>
    private static async Task<IResult> StartDeviceCodeAuth(
        string accountId,
        DeviceCodeAuthManager authManager,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await authManager.StartDeviceCodeFlowAsync(accountId, cancellationToken);
            return Results.Ok(response);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (TimeoutException)
        {
            return Results.StatusCode(504);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                title: "Failed to start device code flow",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Get the status of a pending device code authentication flow.
    /// </summary>
    private static async Task<IResult> GetAuthStatus(
        string accountId,
        DeviceCodeAuthManager authManager,
        IAccountRegistry accountRegistry)
    {
        if (await accountRegistry.GetAccountAsync(accountId) is null)
            return Results.NotFound(new { error = $"Account '{accountId}' not found." });
        var status = authManager.GetFlowStatus(accountId);
        return Results.Ok(status);
    }

    /// <summary>
    /// Cancel a pending device code authentication flow.
    /// </summary>
    private static async Task<IResult> CancelAuth(
        string accountId,
        DeviceCodeAuthManager authManager,
        IAccountRegistry accountRegistry)
    {
        if (await accountRegistry.GetAccountAsync(accountId) is null)
            return Results.NotFound(new { error = $"Account '{accountId}' not found." });
        var cancelled = authManager.CancelFlow(accountId);
        if (cancelled)
        {
            return Results.Ok(new { message = $"Authentication flow for '{accountId}' has been cancelled." });
        }
        return Results.NotFound(new { error = $"No pending authentication flow found for '{accountId}'." });
    }

    /// <summary>
    /// Start a Google OAuth redirect flow. Returns the consent URL plus the relay redirect URI the
    /// operator registers once in their Google OAuth client. <paramref name="returnBase"/> is the
    /// origin the operator's browser is on, so Google's answer comes back to that same address.
    /// </summary>
    private static async Task<IResult> StartGoogleOAuth(
        string accountId,
        string? returnBase,
        HttpContext httpContext,
        IAccountRegistry accountRegistry,
        GoogleOAuthManager oauthManager,
        IOptions<CalendarMcpConfiguration> config)
    {
        var account = await accountRegistry.GetAccountAsync(accountId);
        if (account == null)
        {
            return Results.NotFound(new { error = $"Account '{accountId}' not found." });
        }

        if (!account.ProviderConfig.TryGetValue("clientId", out var clientId) ||
            !account.ProviderConfig.TryGetValue("clientSecret", out var clientSecret))
        {
            return Results.BadRequest(new { error = "Account is missing clientId or clientSecret in configuration." });
        }

        string installCallbackUrl;
        try
        {
            installCallbackUrl = GoogleOAuthRelay.InstallCallbackUrl(returnBase, config.Value.ExternalBaseUrl, httpContext.Request);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        // Headless: the management client opens authUrl in a browser tab and shows redirectUri,
        // the one value the operator must register in the Google OAuth client.
        var redirectUri = config.Value.GoogleOAuthRelayUrl;
        var authUrl = oauthManager.GetAuthorizationUrl(accountId, clientId, clientSecret, redirectUri, installCallbackUrl);
        return Results.Ok(new { authUrl, redirectUri });
    }

    /// <summary>
    /// Handle Google OAuth callback. Exchanges the authorization code for tokens and redirects to the auth UI page.
    /// </summary>
    private static async Task<IResult> GoogleOAuthCallback(
        HttpContext httpContext,
        GoogleOAuthManager oauthManager,
        CancellationToken cancellationToken)
    {
        var query = httpContext.Request.Query;
        var code = query["code"].FirstOrDefault();
        var state = query["state"].FirstOrDefault();
        var error = query["error"].FirstOrDefault();

        if (!string.IsNullOrEmpty(error))
        {
            return OAuthResultPage(false, $"Google returned an error: {error}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return OAuthResultPage(false, "Missing code or state parameter.");
        }

        try
        {
            var accountId = await oauthManager.ExchangeCodeAsync(state, code, cancellationToken);
            return OAuthResultPage(true, $"Account '{accountId}' is now linked. You can close this window.");
        }
        catch (Exception ex)
        {
            return OAuthResultPage(false, ex.Message);
        }
    }

    /// <summary>
    /// Renders a self-contained HTML result for the Google redirect callback. The Blazor
    /// admin UI was removed, so this page only reports the outcome; the management client
    /// polls /admin/accounts/{id}/status to detect the linked state.
    /// </summary>
    private static IResult OAuthResultPage(bool ok, string message)
    {
        var title = ok ? "Connected" : "Connection failed";
        var color = ok ? "#16a34a" : "#dc2626";
        var safe = System.Net.WebUtility.HtmlEncode(message);
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Calendar MCP · {{title}}</title></head>
            <body style="font-family:system-ui,sans-serif;background:#0b0b0c;color:#e5e5e5;display:grid;place-items:center;height:100vh;margin:0">
              <main style="max-width:28rem;text-align:center;padding:2rem">
                <h1 style="color:{{color}};margin:0 0 .5rem">{{title}}</h1>
                <p style="opacity:.85">{{safe}}</p>
              </main>
            </body></html>
            """;
        return Results.Content(html, "text/html");
    }
}
