using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// In-memory account registry loaded from configuration.
/// Subscribes to IOptionsMonitor to hot-reload when the config file changes.
/// </summary>
public class AccountRegistry : IAccountRegistry, IDisposable
{
    private volatile Dictionary<string, AccountInfo> _accounts;
    private readonly ILogger<AccountRegistry> _logger;
    private readonly ITenantContext _tenantContext;
    private readonly IDisposable? _changeSubscription;

    public AccountRegistry(
        IOptionsMonitor<CalendarMcpConfiguration> configuration,
        ILogger<AccountRegistry> logger,
        ITenantContext tenantContext)
    {
        _logger = logger;
        _tenantContext = tenantContext;
        _accounts = BuildAccountsDictionary(configuration.CurrentValue);

        _changeSubscription = configuration.OnChange(config =>
        {
            _logger.LogInformation("Configuration change detected, reloading accounts...");
            _accounts = BuildAccountsDictionary(config);
        });
    }

    private Dictionary<string, AccountInfo> BuildAccountsDictionary(CalendarMcpConfiguration config)
    {
        var accounts = new Dictionary<string, AccountInfo>(StringComparer.OrdinalIgnoreCase);

        if (config.Accounts is { Count: > 0 })
        {
            _logger.LogInformation("Loading {Count} account(s) from configuration...", config.Accounts.Count);

            foreach (var account in config.Accounts)
            {
                var tenantId = TenantIdentity.Normalize(account.TenantId);
                if (!accounts.TryAdd(account.Id, account))
                {
                    throw new InvalidOperationException($"Duplicate account id '{account.Id}' across tenant configuration.");
                }

                var domains = account.Domains.Count > 0
                    ? string.Join(", ", account.Domains)
                    : "(none)";
                var status = account.Enabled ? "enabled" : "disabled";

                _logger.LogInformation(
                    "  Account: {AccountId} | Tenant: {TenantId} | {DisplayName} | Provider: {Provider} | Domains: {Domains} | Status: {Status} | Priority: {Priority}",
                    account.Id,
                    tenantId,
                    account.DisplayName,
                    account.Provider,
                    domains,
                    status,
                    account.Priority);
            }

            var enabledCount = accounts.Values.Count(a => a.Enabled);
            _logger.LogInformation("Account registry initialized: {EnabledCount} enabled, {DisabledCount} disabled",
                enabledCount, accounts.Count - enabledCount);
        }
        else
        {
            _logger.LogWarning("No accounts found in configuration. Add accounts using the CLI: calendar-mcp-cli add-m365-account");
        }

        return accounts;
    }

    public Task<IEnumerable<AccountInfo>> GetAllAccountsAsync()
    {
        return Task.FromResult<IEnumerable<AccountInfo>>(TenantAccounts());
    }

    public Task<AccountInfo?> GetAccountAsync(string accountId)
    {
        var tenantId = _tenantContext.RequireTenantId();
        var account = _accounts.TryGetValue(accountId, out var acc) && OwnedBy(acc, tenantId) ? acc : null;
        return Task.FromResult(account);
    }

    public IEnumerable<AccountInfo> GetEnabledAccounts()
    {
        return TenantAccounts().Where(a => a.Enabled);
    }

    public IEnumerable<AccountInfo> GetAccountsByProvider(string provider)
    {
        return TenantAccounts().Where(a =>
            string.Equals(a.Provider, provider, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<AccountInfo> GetAccountsByDomain(string domain)
    {
        return TenantAccounts().Where(a =>
            a.Domains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)));
    }

    public void Dispose()
    {
        _changeSubscription?.Dispose();
    }

    private IEnumerable<AccountInfo> TenantAccounts()
    {
        var tenantId = _tenantContext.RequireTenantId();
        return _accounts.Values.Where(account => OwnedBy(account, tenantId));
    }

    private static bool OwnedBy(AccountInfo account, string tenantId) =>
        string.Equals(account.TenantId, tenantId, StringComparison.OrdinalIgnoreCase);
}
