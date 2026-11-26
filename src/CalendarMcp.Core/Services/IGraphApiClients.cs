using Microsoft.Graph;

namespace CalendarMcp.Core.Services;

/// <summary>
/// What a Microsoft account offers the code that reads what the provider interface does not expose: an
/// authenticated Graph client (the changes sources), and the username it signed in with (the sending address).
/// </summary>
public interface IGraphApiClients
{
    Task<GraphServiceClient> GraphAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// The username of the Microsoft account the account's tokens are issued to, from MSAL's cache, without
    /// calling Graph; null when nobody is signed in.
    /// </summary>
    Task<string?> SignedInUsernameAsync(string accountId);
}
