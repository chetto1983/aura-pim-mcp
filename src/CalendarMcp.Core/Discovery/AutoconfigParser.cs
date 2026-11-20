using System.Xml;
using System.Xml.Linq;

namespace CalendarMcp.Core.Discovery;

/// <summary>
/// Reads an autoconfig answer (https://wiki.mozilla.org/Thunderbird:Autoconfiguration:ConfigFileFormat). Servers are
/// listed in priority order; the first <c>incomingServer type="imap"</c> and the first <c>outgoingServer type="smtp"</c>
/// that are usable are taken. Usable means a DNS hostname once <c>%EMAILADDRESS%</c>, <c>%EMAILLOCALPART%</c> and
/// <c>%EMAILDOMAIN%</c> are substituted, a port in 1-65535, a <c>socketType</c> of <c>SSL</c> or <c>STARTTLS</c>, and a
/// password <c>authentication</c>. No usable IMAP server is a miss: an OAuth-only or plain answer is no answer here.
/// Anyone can name a domain, so the XML is a stranger's: no DTD, no resolver.
/// </summary>
public static class AutoconfigParser
{
    // Thunderbird's readFromXML.sys.mjs also takes the deprecated "plain" and "secure" for these two.
    private static readonly string[] PasswordAuthentication = ["password-cleartext", "password-encrypted", "plain", "secure"];

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true
    };

    /// <summary>Whether the text is a well-formed document whose root is <c>clientConfig</c>, so a catch-all page is not one.</summary>
    internal static bool IsClientConfig(string xml) => Load(xml)?.Root?.Name.LocalName == "clientConfig";

    public static DiscoveryResult? Parse(string xml, string localPart, string domain)
    {
        var provider = Load(xml)?.Root?.Element("emailProvider");

        var placeholders = new Placeholders(localPart, domain);
        var imap = FirstUsable(provider?.Elements("incomingServer"), "imap", placeholders);
        if (imap is null)
            return null;

        var smtp = FirstUsable(provider!.Elements("outgoingServer"), "smtp", placeholders);
        return new DiscoveryResult(
            DiscoveryKinds.Imap,
            ImapHost: imap.Host, ImapPort: imap.Port, ImapSecurity: imap.Security,
            SmtpHost: smtp?.Host, SmtpPort: smtp?.Port, SmtpSecurity: smtp?.Security,
            Username: imap.Username);
    }

    private static XDocument? Load(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static Server? FirstUsable(IEnumerable<XElement>? servers, string type, Placeholders placeholders) =>
        servers?
            .Where(server => (string?)server.Attribute("type") == type)
            .Select(server => Usable(server, placeholders))
            .FirstOrDefault(server => server is not null);

    private static Server? Usable(XElement server, Placeholders placeholders)
    {
        var host = placeholders.Apply(server.Element("hostname")?.Value.Trim());
        if (host is null || host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns)
            return null;

        if (!int.TryParse(server.Element("port")?.Value.Trim(), out var port) || port is < 1 or > 65535)
            return null;

        var security = server.Element("socketType")?.Value.Trim() switch
        {
            "SSL" => "ssl",
            "STARTTLS" => "starttls",
            _ => null
        };
        if (security is null)
            return null;

        if (!server.Elements("authentication").Any(auth => PasswordAuthentication.Contains(auth.Value.Trim())))
            return null;

        return new Server(host.ToLowerInvariant(), port, security, placeholders.Apply(server.Element("username")?.Value.Trim()));
    }

    private sealed record Server(string Host, int Port, string Security, string? Username);

    private sealed record Placeholders(string LocalPart, string Domain)
    {
        public string? Apply(string? value) => value?
            .Replace("%EMAILADDRESS%", $"{LocalPart}@{Domain}", StringComparison.Ordinal)
            .Replace("%EMAILLOCALPART%", LocalPart, StringComparison.Ordinal)
            .Replace("%EMAILDOMAIN%", Domain, StringComparison.Ordinal);
    }
}
