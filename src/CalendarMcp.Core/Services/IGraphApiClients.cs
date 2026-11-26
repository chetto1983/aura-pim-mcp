using Microsoft.Graph;

namespace CalendarMcp.Core.Services;

/// <summary>An authenticated Microsoft Graph client, for the code that reads what the provider interface does not expose.</summary>
public interface IGraphApiClients
{
    Task<GraphServiceClient> GraphAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// The username of the Microsoft account the account's tokens are issued to, from MSAL's cache, without
    /// calling Graph; null when nobody is signed in.
    /// </summary>
    Task<string?> SignedInUsernameAsync(string accountId);
}
