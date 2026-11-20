using CalendarMcp.Core.Models;
using CalendarMcp.Core.Security;

namespace CalendarMcp.Auth;

/// <summary>
/// Provides CRUD operations on the account configuration file (appsettings.json).
/// This service reads/writes the config file directly — it does NOT use the in-memory
/// IOptionsMonitor pipeline, so changes are visible immediately on the next read.
/// </summary>
public interface IAccountConfigurationService
{
    /// <summary>
    /// Reads all accounts from the config file on disk (fresh read every call).
    /// </summary>
    Task<IReadOnlyList<AccountInfo>> GetAllAccountsFromConfigAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads a single account by ID from the config file on disk.
    /// Returns null if not found.
    /// </summary>
    Task<AccountInfo?> GetAccountFromConfigAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// Adds a new account to the config file. Throws if an account with the same ID already exists.
    /// </summary>
    Task AddAccountAsync(AccountInfo account, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing account in the config file. Throws if not found.
    /// </summary>
    Task UpdateAccountAsync(AccountInfo account, CancellationToken ct = default);

    /// <summary>
    /// Removes an account from the config file. Optionally clears cached credentials.
    /// Throws if not found.
    /// </summary>
    Task RemoveAccountAsync(string accountId, bool clearCredentials = false, CancellationToken ct = default);

    /// <summary>
    /// Clears cached credentials (MSAL cache, Google tokens) for the given account
    /// without removing it from the config file.
    /// </summary>
    Task ClearCredentialsAsync(string accountId, string provider, CancellationToken ct = default);

    /// <summary>
    /// Checks whether an account with the given ID exists in the config file.
    /// </summary>
    Task<bool> AccountExistsAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// Replaces only the provider config of one of the bound tenant's accounts, under the file lock, and only if what
    /// is stored is still <paramref name="expectedStored"/> (keys in any case, values exactly): what was verified is
    /// what gets overwritten. Every other field of the account stays as the file holds it.
    /// </summary>
    /// <exception cref="ConcurrencyException">The stored provider config is no longer <paramref name="expectedStored"/>.</exception>
    /// <exception cref="InvalidOperationException">The tenant has no such account.</exception>
    Task MergeProviderConfigAsync(
        string accountId, IReadOnlyDictionary<string, string> expectedStored,
        IReadOnlyDictionary<string, string> next, CancellationToken ct = default);

    /// <summary>
    /// Encrypts every plaintext secret in every account's provider config, whichever tenant owns it.
    /// Reads the file as a JSON tree, so an account that does not parse is passed over untouched.
    /// Returns how many values it encrypted; the file is written only when that is above zero.
    /// </summary>
    Task<int> ProtectStoredPasswordsAsync(PasswordProtector protector, CancellationToken ct = default);
}
