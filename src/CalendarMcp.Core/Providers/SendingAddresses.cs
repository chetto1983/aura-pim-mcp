using System.Collections.Concurrent;
using System.Globalization;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using Google;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Reads each account's sending address where its provider keeps it: IMAP sends as its username, Gmail as
/// "me", so its address comes from the profile, and a Microsoft account from the username it signed in with.
/// Only the Gmail profile, a call to Google, is cached per account once found; the other two are local reads,
/// so a sign-out or a repair shows at once. A failed lookup is not cached, and never fails the caller.
/// </summary>
public sealed class SendingAddresses(IProviderServiceFactory providers, ILogger<SendingAddresses> logger) : ISendingAddresses
{
    private readonly ConcurrentDictionary<(string Tenant, string Account), string> _gmail = new();

    public async Task<string?> AddressAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        try
        {
            var address = await LookUpAsync(account, cancellationToken);
            return string.IsNullOrWhiteSpace(address) ? null : address;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The code says why it failed; the message can name the address, so it is never logged.
            logger.LogWarning("Sending address of account {AccountId} not found: {ErrorType} {ErrorCode}",
                account.Id, ex.GetType().Name, ErrorCode(ex));
            return null;
        }
    }

    // The failures that carry a code; any other logs "(null)" and its type says why (a gone Google grant is
    // AccountAuthenticationRequiredException, a Microsoft account missing its tenant or client is
    // ProviderOperationException). No ODataError: the Graph branch reads MSAL's cache and calls no Graph.
    private static string? ErrorCode(Exception ex) => ex switch
    {
        GoogleApiException google => ((int)google.HttpStatusCode).ToString(CultureInfo.InvariantCulture),
        MsalException msal => msal.ErrorCode,
        _ => null
    };

    private async Task<string?> LookUpAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        switch (providers.GetProvider(account.Provider))
        {
            case IImapProviderService:
                return account.ProviderConfig.TryGetValue("username", out var username) ? username : null;
            case IGoogleApiClients google:
                return await GmailAddressAsync(google, account, cancellationToken);
            case IGraphApiClients graph:
                // Not GET /me: Graph's "Get user" asks for User.Read, which no scope list here carries
                // (https://learn.microsoft.com/en-us/graph/api/user-get?view=graph-rest-1.0). MSAL requests
                // openid and profile with every token on its own (MSAL.NET 4.88.0 source: OAuth2Value.ReservedScopes,
                // merged in by TokenClient; no Learn page says so), so the cached account carries the id token's preferred_username: "UserPrincipalName
                // (UPN) format ... This can be null" (https://learn.microsoft.com/en-us/dotnet/api/microsoft.identity.client.iaccount.username).
                // The claim is mutable and "can be used for username hints and in human-readable UI"
                // (https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference), which a
                // review's From line is; nothing authorizes on it. It "could be an email address, phone number, or
                // a generic username", and MSAL fills a missing one with the name or a label, hence the '@'.
                var signedIn = await graph.SignedInUsernameAsync(account.Id);
                return signedIn?.Contains('@') == true ? signedIn : null;
            default:
                return null;
        }
    }

    private async Task<string?> GmailAddressAsync(IGoogleApiClients google, AccountInfo account, CancellationToken cancellationToken)
    {
        var key = (account.TenantId, account.Id);
        if (_gmail.TryGetValue(key, out var cached))
            return cached;
        var gmail = await google.GmailAsync(account.Id, cancellationToken);
        var address = (await gmail.Users.GetProfile("me").ExecuteAsync(cancellationToken)).EmailAddress;
        if (!string.IsNullOrWhiteSpace(address))
            _gmail[key] = address;
        return address;
    }
}
