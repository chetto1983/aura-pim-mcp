using System.Text.Json;
using CalendarMcp.Core.Tenancy;

namespace CalendarMcp.Cli.Commands;

internal static class CliTenant
{
    internal const string IdentityVariable = "AURA_IDENTITY_ID";

    internal static string RequireIdentity(string? value = null) =>
        TenantIdentity.Normalize(value ?? Environment.GetEnvironmentVariable(IdentityVariable));

    internal static string AccountId(string localAccountId, string? identity = null)
    {
        var local = localAccountId.Trim();
        if (local.Length == 0)
        {
            throw new ArgumentException("Account ID is required.", nameof(localAccountId));
        }
        return TenantIdentity.AccountId(RequireIdentity(identity), local);
    }

    internal static bool Owns(IReadOnlyDictionary<string, JsonElement> account, string? identity = null) =>
        TryGet(account, "TenantId", "tenantId", out var value) && Owns(ElementText(value), identity);

    internal static bool Owns(JsonElement account, string? identity = null) =>
        account.ValueKind == JsonValueKind.Object &&
        (account.TryGetProperty("TenantId", out var value) || account.TryGetProperty("tenantId", out value)) &&
        Owns(ElementText(value), identity);

    internal static bool HasAccountId(IReadOnlyDictionary<string, object> account, string accountId) =>
        TryGet(account, "Id", "id", out var value) && string.Equals(ValueText(value), accountId, StringComparison.Ordinal);

    private static bool Owns(string? tenantId, string? identity)
    {
        try
        {
            return string.Equals(TenantIdentity.Normalize(tenantId), RequireIdentity(identity), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGet<T>(IReadOnlyDictionary<string, T> values, string first, string second, out T value)
    {
        if (values.TryGetValue(first, out value!))
        {
            return true;
        }
        return values.TryGetValue(second, out value!);
    }

    private static string? ValueText(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.ToString(),
        _ => value?.ToString()
    };

    private static string? ElementText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
