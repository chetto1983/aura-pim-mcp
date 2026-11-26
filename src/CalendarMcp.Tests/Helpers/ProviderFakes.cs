using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;

namespace CalendarMcp.Tests.Helpers;

/// <summary>
/// For reads that make many provider calls in a loop, where the number of lookups is an
/// implementation detail a strict mock would pin.
/// </summary>
internal sealed class SingleAccountRegistry(params AccountInfo[] accounts) : IAccountRegistry
{
    public Task<IEnumerable<AccountInfo>> GetAllAccountsAsync() => Task.FromResult<IEnumerable<AccountInfo>>(accounts);

    public Task<AccountInfo?> GetAccountAsync(string accountId) =>
        Task.FromResult(accounts.FirstOrDefault(a => a.Id == accountId));

    public IEnumerable<AccountInfo> GetEnabledAccounts() => accounts.Where(a => a.Enabled);

    public IEnumerable<AccountInfo> GetAccountsByProvider(string provider) => accounts.Where(a => a.Provider == provider);

}

/// <summary>A signed-in Microsoft account whose token never expires, signed in as <see cref="Username"/>.</summary>
internal sealed class StaticTokenAuthentication(string? username = "signed-in@example.com") : IM365AuthenticationService
{
    /// <summary>Who is signed in; set it to sign in as someone else.</summary>
    public string? Username { get; set; } = username;

    public Task<string?> SignedInUsernameAsync(string tenantId, string clientId, string accountId) =>
        Task.FromResult(Username);

    public Task<string?> GetTokenSilentlyAsync(
        string tenantId, string clientId, string[] scopes, string accountId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("token");

    public Task<bool> HasSignedInUserAsync(string tenantId, string clientId, string accountId) =>
        Task.FromResult(true);

    public Task<string> AuthenticateInteractiveAsync(
        string tenantId, string clientId, string[] scopes, string accountId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> AuthenticateWithDeviceCodeAsync(
        string tenantId, string clientId, string[] scopes, string accountId,
        Func<string, Task> deviceCodeCallback, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>Sending addresses by account id; an account it does not name has none.</summary>
internal sealed class FixedSendingAddresses(params (string AccountId, string Address)[] addresses) : ISendingAddresses
{
    public Task<string?> AddressAsync(AccountInfo account, CancellationToken cancellationToken) =>
        Task.FromResult(addresses.Where(a => a.AccountId == account.Id).Select(a => (string?)a.Address).FirstOrDefault());
}
