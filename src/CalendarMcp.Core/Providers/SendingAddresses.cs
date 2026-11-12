using System.Collections.Concurrent;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Reads each account's sending address where its provider keeps it: IMAP sends as its username, Gmail and
/// Graph send as "me", so their address comes from the profile. A found address is cached per account; a
/// failed lookup is not, and never fails the caller.
/// </summary>
public sealed class SendingAddresses(IProviderServiceFactory providers, ILogger<SendingAddresses> logger) : ISendingAddresses
{
    private readonly ConcurrentDictionary<(string Tenant, string Account), string> _found = new();

    public async Task<string?> AddressAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        var key = (account.TenantId, account.Id);
        if (_found.TryGetValue(key, out var cached))
            return cached;
        try
        {
            var address = await LookUpAsync(account, cancellationToken);
            if (!string.IsNullOrWhiteSpace(address))
                _found[key] = address;
            return string.IsNullOrWhiteSpace(address) ? null : address;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Sending address of account {AccountId} not found: {ErrorType}", account.Id, ex.GetType().Name);
            return null;
        }
    }

    private async Task<string?> LookUpAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        switch (providers.GetProvider(account.Provider))
        {
            case IImapProviderService:
                return account.ProviderConfig.TryGetValue("username", out var username) ? username : null;
            case IGoogleApiClients google:
                var gmail = await google.GmailAsync(account.Id, cancellationToken);
                return (await gmail.Users.GetProfile("me").ExecuteAsync(cancellationToken)).EmailAddress;
            case IGraphApiClients graph:
                var client = await graph.GraphAsync(account.Id, cancellationToken);
                var me = await client.Me.GetAsync(
                    request => request.QueryParameters.Select = ["mail", "userPrincipalName"], cancellationToken);
                return string.IsNullOrWhiteSpace(me?.Mail) ? me?.UserPrincipalName : me.Mail;
            default:
                return null;
        }
    }
}
