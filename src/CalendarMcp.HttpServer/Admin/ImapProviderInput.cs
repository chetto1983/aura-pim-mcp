using System.Net;
using CalendarMcp.Core.Providers;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// What an IMAP provider config must be before the verifier sees it, so that a check fails only on what a mail server
/// does (the precondition of <see cref="IImapAccountVerifier"/>). A write (create, PATCH) also carries TLS: every
/// account it stores names <c>imapSecurity</c> and <c>smtpSecurity</c>, <c>ssl</c> or <c>starttls</c> (spec
/// amendment A2), because MailKit's <c>Auto</c> logs in in clear when a server offers no STARTTLS.
/// </summary>
/// <remarks>The dictionaries read here match keys in any case, as every reader of the account file does.</remarks>
internal static class ImapProviderInput
{
    public const string TlsRequired = "tls_required";
    public const string InvalidSecurity = "invalid_security";
    public const string InvalidPort = "invalid_port";
    public const string InvalidHost = "invalid_host";
    public const string DuplicateKey = "duplicate_key";
    public const string ServerOwnedKey = "server_owned_key";

    private const string Ssl = "ssl";
    private const string StartTls = "starttls";
    private const string None = "none";

    // The longest name DNS carries (RFC 1035 section 2.3.4); the resolver throws on a longer one.
    private const int MaxHostLength = 253;

    private static readonly (string Host, string Security, string Port, int DefaultPort)[] Servers =
    [
        ("imapHost", "imapSecurity", "imapPort", ImapProviderService.DefaultImapPort),
        ("smtpHost", "smtpSecurity", "smtpPort", ImapProviderService.DefaultSmtpPort)
    ];

    /// <summary>The keys that choose the servers: each one's host, port and security.</summary>
    public static IEnumerable<string> ServerKeys => Servers.SelectMany(server => new[] { server.Host, server.Port, server.Security });

    /// <summary>Two keys that differ only in case: which of them a reader takes would be an accident.</summary>
    public static bool HasDuplicateKeys(IEnumerable<string> keys) =>
        keys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);

    /// <summary>
    /// A key only the server writes. Like <c>none</c> for TLS, a value the server would have to override is refused,
    /// not silently replaced: no client has a reason to send it, since no answer ever carries a provider config.
    /// </summary>
    public static bool NamesServerOwnedKey(IEnumerable<string> keys) =>
        keys.Any(key => string.Equals(key, ImapProviderService.AllowPrivateHostsKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// For what a create or a PATCH would store: host names a resolver takes, ports in 1-65535, and each security
    /// key <c>ssl</c> or <c>starttls</c>, or taken from its port when absent (993 and 465 are <c>ssl</c>, any other
    /// <c>starttls</c>). Answers the config with both keys written in lower case, or the error. A non-null
    /// <paramref name="allowPrivateHosts"/>, the permission of a request that chooses these servers, replaces what
    /// was stored, and every later connection for the account is held to it; null keeps the key as it is.
    /// <paramref name="config"/> has no duplicate keys (<see cref="HasDuplicateKeys"/>).
    /// </summary>
    public static (Dictionary<string, string>? Config, string? Error) ForWrite(
        IReadOnlyDictionary<string, string> config, bool? allowPrivateHosts)
    {
        var result = new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase);
        foreach (var (hostKey, securityKey, portKey, defaultPort) in Servers)
        {
            if (!IsHost(result, hostKey))
                return (null, InvalidHost);
            if (!TryPort(result, portKey, defaultPort, out var port))
                return (null, InvalidPort);

            switch (Value(result, securityKey)?.ToLowerInvariant())
            {
                case null:
                    result[securityKey] = port is 993 or 465 ? Ssl : StartTls;
                    break;
                case Ssl or StartTls:
                    result[securityKey] = result[securityKey].ToLowerInvariant();
                    break;
                case None:
                    return (null, TlsRequired);
                default:
                    return (null, InvalidSecurity);
            }
        }

        if (allowPrivateHosts is { } allow)
        {
            // Removed first: the dictionary ignores case, and a stored key in another case would keep its spelling.
            result.Remove(ImapProviderService.AllowPrivateHostsKey);
            result[ImapProviderService.AllowPrivateHostsKey] = allow ? "true" : "false";
        }

        return (result, null);
    }

    /// <summary>
    /// For an account as stored: what the verifier needs to run. A missing security key passes (an account stored
    /// before the keys existed), and so does <c>none</c>, which only the operator can have written: «Prova di nuovo»
    /// checks the account the way the watch reads it.
    /// </summary>
    public static bool IsVerifiable(IReadOnlyDictionary<string, string> stored) =>
        Value(stored, "username") is not null &&
        Value(stored, "password") is not null &&
        Servers.All(server =>
            IsHost(stored, server.Host) &&
            TryPort(stored, server.Port, server.DefaultPort, out _) &&
            Value(stored, server.Security)?.ToLowerInvariant() is null or Ssl or StartTls or None);

    // Blank is absent, as ImapAccountConfig.From reads it.
    private static string? Value(IReadOnlyDictionary<string, string> config, string key) =>
        config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    // Absent passes: a create or a PATCH requires the hosts elsewhere (TenantProviderConfig), and a stored account
    // without one is read with the provider's default, as the watch reads it.
    private static bool IsHost(IReadOnlyDictionary<string, string> config, string key) =>
        Value(config, key) is not { } host ||
        (host.Length <= MaxHostLength &&
         Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6);

    private static bool TryPort(IReadOnlyDictionary<string, string> config, string key, int defaultPort, out int port)
    {
        var value = Value(config, key);
        if (value is null)
        {
            port = defaultPort;
            return true;
        }

        return int.TryParse(value, out port) && port is > 0 and <= IPEndPoint.MaxPort;
    }
}
