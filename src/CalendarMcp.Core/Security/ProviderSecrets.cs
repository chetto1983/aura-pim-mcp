namespace CalendarMcp.Core.Security;

/// <summary>
/// Knows which <c>ProviderConfig</c> keys hold a secret and stores them encrypted.
/// Restores what upstream's own admin form did on save (<c>AccountFormModels.cs</c>
/// at MarimerLLC/calendar-mcp 3b2762b): protect the IMAP password before it is written.
/// </summary>
public static class ProviderSecrets
{
    /// <summary>True for a <c>ProviderConfig</c> key whose value is a secret, in any case.</summary>
    public static bool IsSecretKey(string key) =>
        string.Equals(key, "password", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a copy of an incoming provider config with every non-empty secret encrypted.
    /// A value that already starts with <c>ENC:</c> is encrypted again: the prefix is
    /// something a caller can type, so it proves nothing about the value.
    /// </summary>
    public static Dictionary<string, string> ProtectIncoming(
        IReadOnlyDictionary<string, string> config, PasswordProtector protector)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in config)
            result[key] = IsSecretKey(key) && !string.IsNullOrEmpty(value) ? protector.Protect(value) : value;
        return result;
    }
}
