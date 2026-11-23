using System.Text.Json;

namespace CalendarMcp.Core.Discovery;

/// <summary>
/// One provider of <c>presets.json</c>. Every value is copied from the provider's own page, cited in <c>Source</c>.
/// <c>Domains</c> match an address's domain; <c>MxSuffixes</c> match only a suffix of an MX host, for a provider
/// that hosts other people's domains. <c>Username</c> is <c>address</c> or <c>localpart</c>.
/// </summary>
internal sealed record MailPreset(
    string[] Domains,
    string[]? MxSuffixes,
    string Kind,
    string? ImapHost,
    int? ImapPort,
    string? ImapSecurity,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpSecurity,
    string Username,
    AppPasswordHint? AppPassword,
    bool? PasswordFallback,
    string Source)
{
    public DiscoveryResult Answer(DiscoveryAddress address, string source) => new(
        Kind, source, ImapHost, ImapPort, ImapSecurity, SmtpHost, SmtpPort, SmtpSecurity,
        Username == "localpart" ? address.LocalPart : $"{address.LocalPart}@{address.Domain}",
        AppPassword, PasswordFallback);
}

/// <summary>The preset table, embedded in the assembly, and the MX hosts that name a provider outright.</summary>
internal static class MailPresets
{
    private static readonly Lazy<MailPreset[]> Table = new(Load);

    public static IReadOnlyList<MailPreset> All => Table.Value;

    public static MailPreset? ForDomain(string domain) =>
        Table.Value.FirstOrDefault(preset => preset.Domains.Contains(domain));

    public static MailPreset? ForMxSuffix(string suffix) =>
        Table.Value.FirstOrDefault(preset => (preset.MxSuffixes ?? []).Contains(suffix) || preset.Domains.Contains(suffix));

    /// <summary>
    /// Google and Microsoft by the MX host alone, before the suffix walk: <c>outlook.com</c> is a consumer address
    /// domain, while <c>*.mail.protection.outlook.com</c> and any other <c>*.outlook.com</c> MX is Microsoft 365, and
    /// only <c>*.olc.protection.outlook.com</c> is Outlook.com. Microsoft 365 has no preset: it is connected by
    /// signing in, never by password.
    /// </summary>
    public static DiscoveryResult? ByMxHost(string host, DiscoveryAddress address)
    {
        if (IsUnder(host, "google.com") || IsUnder(host, "googlemail.com"))
            return OfKind(DiscoveryKinds.Google).Answer(address, "preset-mx");
        if (IsUnder(host, "olc.protection.outlook.com"))
            return OfKind(DiscoveryKinds.OutlookCom).Answer(address, "preset-mx");
        if (IsUnder(host, "outlook.com"))
            return new DiscoveryResult(DiscoveryKinds.Microsoft365, "preset-mx");
        return null;
    }

    private static bool IsUnder(string host, string domain) => host.EndsWith("." + domain, StringComparison.Ordinal);

    private static MailPreset OfKind(string kind) => Table.Value.Single(preset => preset.Kind == kind);

    private static MailPreset[] Load()
    {
        using var stream = typeof(MailPresets).Assembly.GetManifestResourceStream("discovery/presets.json")
            ?? throw new InvalidOperationException("The embedded resource discovery/presets.json is missing.");
        return JsonSerializer.Deserialize<MailPreset[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("discovery/presets.json is empty.");
    }
}
