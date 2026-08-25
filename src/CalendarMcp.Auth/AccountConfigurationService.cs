using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Tenancy;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Auth;

/// <summary>
/// Reads and writes account configuration directly to/from the appsettings.json file
/// using System.Text.Json.Nodes for mutable DOM manipulation.
/// Thread-safe via SemaphoreSlim for in-process concurrency.
/// </summary>
public sealed class AccountConfigurationService : IAccountConfigurationService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly ILogger<AccountConfigurationService> _logger;
    private readonly ITenantContext _tenantContext;

    public AccountConfigurationService(
        ILogger<AccountConfigurationService> logger,
        ITenantContext tenantContext)
    {
        _logger = logger;
        _tenantContext = tenantContext;
    }

    public async Task<IReadOnlyList<AccountInfo>> GetAllAccountsFromConfigAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var (_, accountsArray) = await ReadConfigAsync(ct);
            var tenantId = _tenantContext.RequireTenantId();
            return ParseAccounts(accountsArray)
                .Where(account => OwnedBy(account, tenantId))
                .ToList();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<AccountInfo?> GetAccountFromConfigAsync(string accountId, CancellationToken ct = default)
    {
        var accounts = await GetAllAccountsFromConfigAsync(ct);
        return accounts.FirstOrDefault(a => a.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task AddAccountAsync(AccountInfo account, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var (root, accountsArray) = await ReadConfigAsync(ct);

            RequireOwned(account);

            // Check for duplicate
            if (FindAccountIndex(accountsArray, account.Id) >= 0)
                throw new InvalidOperationException($"Account '{account.Id}' already exists.");

            accountsArray.Add(AccountInfoToNode(account));
            await WriteConfigAsync(root, ct);

            _logger.LogInformation("Added account '{AccountId}' ({Provider})", account.Id, account.Provider);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task UpdateAccountAsync(AccountInfo account, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var (root, accountsArray) = await ReadConfigAsync(ct);

            RequireOwned(account);

            var index = FindAccountIndex(accountsArray, account.Id);
            if (index < 0 || !NodeOwnedBy(accountsArray[index], account.TenantId))
                throw new InvalidOperationException($"Account '{account.Id}' not found.");

            accountsArray[index] = AccountInfoToNode(account);
            await WriteConfigAsync(root, ct);

            _logger.LogInformation("Updated account '{AccountId}'", account.Id);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task RemoveAccountAsync(string accountId, bool clearCredentials = false, CancellationToken ct = default)
    {
        string? provider = null;

        await _fileLock.WaitAsync(ct);
        try
        {
            var (root, accountsArray) = await ReadConfigAsync(ct);
            var tenantId = _tenantContext.RequireTenantId();

            var index = FindAccountIndex(accountsArray, accountId);
            if (index < 0 || !NodeOwnedBy(accountsArray[index], tenantId))
                throw new InvalidOperationException($"Account '{accountId}' not found.");

            // Capture provider before removal for credential clearing
            var node = accountsArray[index]?.AsObject();
            provider = GetStringProperty(node, "Provider");

            accountsArray.RemoveAt(index);
            await WriteConfigAsync(root, ct);

            _logger.LogInformation("Removed account '{AccountId}'", accountId);
        }
        finally
        {
            _fileLock.Release();
        }

        if (clearCredentials && provider is not null)
        {
            ClearCredentials(accountId, provider);
        }
    }

    public async Task ClearCredentialsAsync(string accountId, string provider, CancellationToken ct = default)
    {
        if (await GetAccountFromConfigAsync(accountId, ct) is null)
            throw new InvalidOperationException($"Account '{accountId}' not found.");
        ClearCredentials(accountId, provider);
    }

    private void ClearCredentials(string accountId, string provider)
    {
        switch (provider.ToLowerInvariant())
        {
            case "microsoft365" or "m365" or "outlook.com" or "outlook" or "hotmail":
                ClearMicrosoftCredentials(accountId);
                break;
            case "google" or "gmail" or "google workspace":
                ClearGoogleCredentials(accountId);
                break;
            default:
                _logger.LogDebug("No credentials to clear for provider '{Provider}'", provider);
                break;
        }
    }

    public async Task<bool> AccountExistsAsync(string accountId, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var (_, accountsArray) = await ReadConfigAsync(ct);
            var index = FindAccountIndex(accountsArray, accountId);
            return index >= 0 && NodeOwnedBy(accountsArray[index], _tenantContext.RequireTenantId());
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static string GetConfigPath()
    {
        ConfigurationPaths.EnsureConfigFileExists();
        return ConfigurationPaths.GetConfigFilePath();
    }

    /// <summary>
    /// Reads the config file and returns the root JsonObject and the Accounts JsonArray.
    /// Creates the CalendarMcp.Accounts path if it doesn't exist.
    /// </summary>
    private static async Task<(JsonObject Root, JsonArray Accounts)> ReadConfigAsync(CancellationToken ct)
    {
        var configPath = GetConfigPath();
        var json = await File.ReadAllTextAsync(configPath, ct);
        var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

        // Navigate CalendarMcp → Accounts, creating if missing
        if (root["CalendarMcp"] is not JsonObject calendarMcp)
        {
            calendarMcp = new JsonObject();
            root["CalendarMcp"] = calendarMcp;
        }

        if (calendarMcp["Accounts"] is not JsonArray accounts)
        {
            accounts = new JsonArray();
            calendarMcp["Accounts"] = accounts;
        }

        return (root, accounts);
    }

    private static async Task WriteConfigAsync(JsonObject root, CancellationToken ct)
    {
        var configPath = GetConfigPath();
        var json = root.ToJsonString(WriteOptions);
        await File.WriteAllTextAsync(configPath, json, ct);
    }

    /// <summary>
    /// Finds the index of an account in the array by ID (case-insensitive, supports both PascalCase and camelCase).
    /// </summary>
    private static int FindAccountIndex(JsonArray accounts, string accountId)
    {
        for (var i = 0; i < accounts.Count; i++)
        {
            var obj = accounts[i]?.AsObject();
            var id = GetStringProperty(obj, "Id");
            if (id is not null && id.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Gets a string property from a JsonObject, trying PascalCase first then camelCase.
    /// </summary>
    private static string? GetStringProperty(JsonObject? obj, string pascalName)
    {
        if (obj is null) return null;

        // Try PascalCase
        if (obj[pascalName] is JsonNode pascal)
            return pascal.GetValue<string>();

        // Try camelCase
        var camelName = char.ToLowerInvariant(pascalName[0]) + pascalName[1..];
        if (obj[camelName] is JsonNode camel)
            return camel.GetValue<string>();

        return null;
    }

    /// <summary>
    /// Converts an AccountInfo to a JsonNode (always PascalCase output).
    /// </summary>
    private static JsonNode AccountInfoToNode(AccountInfo account)
    {
        var obj = new JsonObject
        {
            ["Id"] = account.Id,
            ["TenantId"] = account.TenantId,
            ["DisplayName"] = account.DisplayName,
            ["Provider"] = account.Provider,
            ["Enabled"] = account.Enabled,
            ["Priority"] = account.Priority,
            ["Domains"] = new JsonArray(account.Domains.Select(d => JsonValue.Create(d)).ToArray<JsonNode?>()),
            ["ProviderConfig"] = DictionaryToNode(account.ProviderConfig)
        };
        return obj;
    }

    private static JsonObject DictionaryToNode(Dictionary<string, string> dict)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in dict)
        {
            obj[key] = value;
        }
        return obj;
    }

    /// <summary>
    /// Parses a JsonArray of accounts into a list of AccountInfo objects.
    /// Handles both PascalCase and camelCase properties.
    /// </summary>
    private static List<AccountInfo> ParseAccounts(JsonArray accountsArray)
    {
        var results = new List<AccountInfo>();

        foreach (var node in accountsArray)
        {
            var obj = node?.AsObject();
            if (obj is null) continue;

            var id = GetStringProperty(obj, "Id");
            var displayName = GetStringProperty(obj, "DisplayName");
            var provider = GetStringProperty(obj, "Provider");
            var tenantId = GetStringProperty(obj, "TenantId");

            if (id is null || displayName is null || provider is null || tenantId is null)
                throw new InvalidDataException("Every Calendar account must have Id, TenantId, DisplayName, and Provider.");

            tenantId = TenantIdentity.Normalize(tenantId);

            var enabled = GetBoolProperty(obj, "Enabled") ?? true;
            var priority = GetIntProperty(obj, "Priority") ?? 0;
            var domains = GetStringListProperty(obj, "Domains");
            var providerConfig = GetStringDictProperty(obj, "ProviderConfig");

            results.Add(new AccountInfo
            {
                Id = id,
                TenantId = tenantId,
                DisplayName = displayName,
                Provider = provider,
                Enabled = enabled,
                Priority = priority,
                Domains = domains,
                ProviderConfig = providerConfig
            });
        }

        return results;
    }

    private static bool? GetBoolProperty(JsonObject? obj, string pascalName)
    {
        if (obj is null) return null;
        var camelName = char.ToLowerInvariant(pascalName[0]) + pascalName[1..];
        if (obj[pascalName] is JsonNode p) return p.GetValue<bool>();
        if (obj[camelName] is JsonNode c) return c.GetValue<bool>();
        return null;
    }

    private static int? GetIntProperty(JsonObject? obj, string pascalName)
    {
        if (obj is null) return null;
        var camelName = char.ToLowerInvariant(pascalName[0]) + pascalName[1..];
        if (obj[pascalName] is JsonNode p) return p.GetValue<int>();
        if (obj[camelName] is JsonNode c) return c.GetValue<int>();
        return null;
    }

    private static List<string> GetStringListProperty(JsonObject? obj, string pascalName)
    {
        if (obj is null) return [];
        var camelName = char.ToLowerInvariant(pascalName[0]) + pascalName[1..];
        var arr = obj[pascalName]?.AsArray() ?? obj[camelName]?.AsArray();
        if (arr is null) return [];
        return arr.Select(n => n?.GetValue<string>()).Where(s => s is not null).ToList()!;
    }

    private static Dictionary<string, string> GetStringDictProperty(JsonObject? obj, string pascalName)
    {
        if (obj is null) return [];
        var camelName = char.ToLowerInvariant(pascalName[0]) + pascalName[1..];
        var dictObj = obj[pascalName]?.AsObject() ?? obj[camelName]?.AsObject();
        if (dictObj is null) return [];

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in dictObj)
        {
            if (prop.Value is not null)
                result[prop.Key] = prop.Value.GetValue<string>();
        }
        return result;
    }

    private void ClearMicrosoftCredentials(string accountId)
    {
        var cachePath = ConfigurationPaths.GetMsalCachePath(accountId);
        if (File.Exists(cachePath))
        {
            try
            {
                File.Delete(cachePath);
                _logger.LogInformation("Deleted MSAL token cache for account '{AccountId}'", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete MSAL token cache for account '{AccountId}'", accountId);
            }
        }
    }

    private void ClearGoogleCredentials(string accountId)
    {
        var credDir = ConfigurationPaths.GetGoogleCredentialsDirectory(accountId);
        if (Directory.Exists(credDir))
        {
            try
            {
                Directory.Delete(credDir, recursive: true);
                _logger.LogInformation("Deleted Google credentials for account '{AccountId}'", accountId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete Google credentials for account '{AccountId}'", accountId);
            }
        }
    }

    private void RequireOwned(AccountInfo account)
    {
        var tenantId = _tenantContext.RequireTenantId();
        if (!OwnedBy(account, tenantId))
            throw new InvalidOperationException($"Account '{account.Id}' not found.");
    }

    private static bool OwnedBy(AccountInfo account, string tenantId) =>
        string.Equals(account.TenantId, tenantId, StringComparison.OrdinalIgnoreCase);

    private static bool NodeOwnedBy(JsonNode? node, string tenantId)
    {
        var configured = GetStringProperty(node?.AsObject(), "TenantId");
        return configured is not null &&
            string.Equals(TenantIdentity.Normalize(configured), tenantId, StringComparison.OrdinalIgnoreCase);
    }
}
