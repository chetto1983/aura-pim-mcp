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

    public IEnumerable<AccountInfo> GetAccountsByDomain(string domain) => accounts.Where(a => a.Domains.Contains(domain));
}

/// <summary>A signed-in Microsoft account whose token never expires.</summary>
internal sealed class StaticTokenAuthentication : IM365AuthenticationService
{
    public Task<string?> GetTokenSilentlyAsync(
        string tenantId, string clientId, string[] scopes, string accountId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("token");

    public Task<string> AuthenticateInteractiveAsync(
        string tenantId, string clientId, string[] scopes, string accountId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> AuthenticateWithDeviceCodeAsync(
        string tenantId, string clientId, string[] scopes, string accountId,
        Func<string, Task> deviceCodeCallback, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
