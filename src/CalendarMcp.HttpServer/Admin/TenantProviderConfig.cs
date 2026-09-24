using CalendarMcp.Auth;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// Provider-config validation for an account a TENANT writes through the admin API: upstream's
/// per-provider rules, plus a refusal of every key that makes this server read a file from its
/// own disk. A JSON account's local paths read whatever they name -- another tenant's Google
/// token file included. Local JSON files stay available to accounts the operator writes into
/// the configuration itself.
/// </summary>
/// <remarks>
/// The rule is on the key names in any casing, not on the value of <c>source</c> alone:
/// upstream's validator matches keys case-insensitively while <c>JsonCalendarProviderService</c>
/// reads them case-sensitively and treats a missing exact <c>source</c> key as local, so
/// <c>{"Source":"onedrive","filePath":...}</c> would pass the one and read a file in the other.
/// </remarks>
internal static class TenantProviderConfig
{
    private static readonly string[] ServerFileKeys = ["filePath", "emailsFilePath", "contactsFilePath"];

    internal static (bool IsValid, string? Error) Validate(string provider, Dictionary<string, string>? config)
    {
        config ??= [];
        var (valid, error) = AccountValidation.ValidateProviderConfig(provider, config);
        if (!valid || !provider.Equals("json", StringComparison.OrdinalIgnoreCase))
            return (valid, error);

        var readsServerFiles =
            config.Any(kv => kv.Key.Equals("source", StringComparison.OrdinalIgnoreCase) &&
                              !kv.Value.Equals("onedrive", StringComparison.OrdinalIgnoreCase)) ||
            config.Keys.Any(key => ServerFileKeys.Contains(key, StringComparer.OrdinalIgnoreCase));
        return readsServerFiles
            ? (false, "A JSON account added through the admin API must use source 'onedrive' and no local file path: this server does not read its own disk on a tenant's behalf.")
            : (true, null);
    }
}
