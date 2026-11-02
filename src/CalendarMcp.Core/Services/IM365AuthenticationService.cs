using CalendarMcp.Core.Models;

namespace CalendarMcp.Core.Services;

/// <summary>
/// Service for authenticating M365 accounts
/// </summary>
public interface IM365AuthenticationService
{
    /// <summary>
    /// Authenticate interactively via browser popup (for M365 organizational accounts)
    /// </summary>
    /// <param name="tenantId">Azure AD tenant ID</param>
    /// <param name="clientId">Application client ID</param>
    /// <param name="scopes">Required scopes</param>
    /// <param name="accountId">Unique account identifier for token cache</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Access token</returns>
    Task<string> AuthenticateInteractiveAsync(
        string tenantId,
        string clientId,
        string[] scopes,
        string accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticate using Device Code Flow (for Outlook.com/MSA personal accounts)
    /// </summary>
    /// <param name="tenantId">Azure AD tenant ID (typically "consumers" for MSA)</param>
    /// <param name="clientId">Application client ID</param>
    /// <param name="scopes">Required scopes</param>
    /// <param name="accountId">Unique account identifier for token cache</param>
    /// <param name="deviceCodeCallback">Callback to display the device code to user</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Access token</returns>
    Task<string> AuthenticateWithDeviceCodeAsync(
        string tenantId,
        string clientId,
        string[] scopes,
        string accountId,
        Func<string, Task> deviceCodeCallback,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get token silently from cache (for MCP server runtime)
    /// </summary>
    /// <param name="tenantId">Azure AD tenant ID</param>
    /// <param name="clientId">Application client ID</param>
    /// <param name="scopes">Required scopes</param>
    /// <param name="accountId">Unique account identifier for token cache</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>
    /// Access token, or null when the account must sign in again. A failure that says nothing
    /// about the grant, such as a network error during the refresh, throws instead.
    /// </returns>
    Task<string?> GetTokenSilentlyAsync(
        string tenantId,
        string clientId,
        string[] scopes,
        string accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the account's token cache holds a signed-in user. It reads the cache only, so
    /// polling it never calls Microsoft.
    /// </summary>
    Task<bool> HasSignedInUserAsync(string tenantId, string clientId, string accountId);
}
