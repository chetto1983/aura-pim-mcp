namespace CalendarMcp.Core.Changes;

/// <summary>The provider families the changes contract serves, under the names accounts use.</summary>
public static class ChangesProviders
{
    public const string Google = "google";
    public const string Microsoft365 = "microsoft365";
    public const string Outlook = "outlook";
    public const string Imap = "imap";

    /// <summary>The family of an account's provider, or null for one with no changes source yet.</summary>
    public static string? Family(string provider) => provider.ToLowerInvariant() switch
    {
        "google" or "gmail" or "google workspace" => Google,
        "microsoft365" or "m365" => Microsoft365,
        "outlook.com" or "outlook" or "hotmail" => Outlook,
        "imap" or "imap-smtp" => Imap,
        _ => null
    };
}
