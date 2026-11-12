using CalendarMcp.Core.Models;

namespace CalendarMcp.Core.Services;

/// <summary>
/// The address an account sends mail from, so a client can show who a message will leave as.
/// </summary>
public interface ISendingAddresses
{
    /// <summary>The account's sending address, or null when it sends no mail or the lookup failed.</summary>
    Task<string?> AddressAsync(AccountInfo account, CancellationToken cancellationToken);
}
