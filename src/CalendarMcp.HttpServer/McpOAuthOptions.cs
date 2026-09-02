using System.Security.Cryptography;
using System.Text;

namespace CalendarMcp.HttpServer;

/// <summary>
/// One authorization server whose tokens are accepted, with the discovery document that
/// yields its keys. Two values and not one because they are not always the same host:
/// Compose runs a split horizon where the issuer is advertised as 127.0.0.1:9080 (the
/// name a client on the host can reach) while metadata is fetched from aura:9080 (the
/// name this container can reach).
/// </summary>
internal sealed record TrustedIssuer(string Issuer, string MetadataAddress)
{
    internal static TrustedIssuer Of(string issuer, string? metadataAddress = null)
    {
        issuer = issuer.Trim().TrimEnd('/');
        return new TrustedIssuer(
            issuer,
            string.IsNullOrWhiteSpace(metadataAddress)
                ? $"{issuer}/.well-known/oauth-authorization-server"
                : metadataAddress!.Trim());
    }
}

internal sealed record McpOAuthOptions(
    IReadOnlyList<TrustedIssuer> Issuers,
    string Resource,
    string ToolsScope)
{
    /// <summary>
    /// The authorization server this deployment owns. Its subjects are tenant GUIDs and
    /// every store already on disk is named after one, so they pass through unchanged.
    /// </summary>
    internal TrustedIssuer Home => Issuers[0];

    internal static McpOAuthOptions FromConfiguration(IConfiguration configuration)
    {
        var home = TrustedIssuer.Of(
            Value(configuration, "OAuth:Issuer", "http://localhost:9080"),
            configuration["OAuth:MetadataAddress"]);
        var issuers = new List<TrustedIssuer> { home };
        issuers.AddRange(ParseTrustedIssuers(configuration["OAuth:TrustedIssuers"]));
        return new McpOAuthOptions(
            issuers,
            Value(configuration, "OAuth:Resource", "http://localhost:8080/"),
            Value(configuration, "OAuth:ToolsScope", "mcp:tools"));
    }

    /// <summary>
    /// Reads OAuth:TrustedIssuers — a comma-separated list of <c>issuer</c> or
    /// <c>issuer=metadata_address</c> entries naming authorization servers OTHER than the
    /// home one. Trusting exactly one was never a protocol requirement: the MCP
    /// specification (revision 2026-07-28, basic/authorization) makes an MCP server an
    /// OAuth resource server and says its authorization server "may be hosted with the
    /// resource server or a separate entity". Blank entries are dropped rather than
    /// becoming an issuer named "" that no token could match but that would still sit in
    /// the trusted set.
    /// </summary>
    internal static IEnumerable<TrustedIssuer> ParseTrustedIssuers(string? raw)
    {
        foreach (var entry in (raw ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=');
            var issuer = separator < 0 ? entry : entry[..separator];
            var metadata = separator < 0 ? null : entry[(separator + 1)..];
            if (!string.IsNullOrWhiteSpace(issuer))
            {
                yield return TrustedIssuer.Of(issuer, metadata);
            }
        }
    }

    internal TrustedIssuer? IssuerNamed(string? name)
    {
        // Exact match only. An issuer is the root of trust, so prefix or suffix tolerance
        // here would let a lookalike host mint identities.
        var wanted = name?.Trim().TrimEnd('/');
        return string.IsNullOrEmpty(wanted)
            ? null
            : Issuers.FirstOrDefault(issuer => string.Equals(issuer.Issuer, wanted, StringComparison.Ordinal));
    }

    /// <summary>
    /// Maps an authenticated (issuer, subject) pair onto the tenant it may reach.
    /// <para>
    /// RFC 7519 §4.1.2 guarantees <c>sub</c> is unique only WITHIN one issuer's namespace.
    /// The moment a second issuer is trusted, keying on <c>sub</c> alone is not merely
    /// untidy: a foreign account named after an existing tenant GUID would be handed that
    /// person's mailboxes and calendars.
    /// </para>
    /// <para>
    /// The home issuer is the exception, deliberately — its subjects ARE the tenant GUIDs
    /// already on disk, so passing them through means widening the trusted set migrates
    /// nothing. Foreign subjects fold into a name-based (version 5) UUID because
    /// TenantIdentity.Normalize requires a GUID: deterministic, so the same person returns
    /// to the same tenant, and needing no registry to translate it back.
    /// </para>
    /// </summary>
    internal string TenantIdentityFor(string? issuer, string subject)
    {
        if (string.Equals(issuer?.Trim().TrimEnd('/'), Home.Issuer, StringComparison.Ordinal))
        {
            return subject;
        }
        // The separator cannot appear in either half, so no two distinct pairs can be
        // spelled as the same joined string.
        return NameBasedGuid(ForeignIdentityNamespace, $"{issuer}\n{subject}").ToString("D");
    }

    /// <summary>
    /// Anchors the derivation above. Computed from a fixed URL rather than written as a
    /// literal, so the value is reproducible by reading this line.
    /// </summary>
    internal static readonly Guid ForeignIdentityNamespace =
        NameBasedGuid(Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8"), "https://aura.local/mcp/foreign-identity");

    /// <summary>RFC 4122 §4.3 name-based UUID, SHA-1 (version 5).</summary>
    private static Guid NameBasedGuid(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        // ToByteArray() emits the first three fields little-endian; RFC 4122 hashes them
        // big-endian, so a Guid produced without this swap would not match any other
        // implementation of the same namespace and name.
        SwapToBigEndian(namespaceBytes);

        var hash = SHA1.HashData([.. namespaceBytes, .. Encoding.UTF8.GetBytes(name)]);
        var guidBytes = hash[..16];
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // version 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant

        SwapToBigEndian(guidBytes);
        return new Guid(guidBytes);
    }

    private static void SwapToBigEndian(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }

    private static string Value(IConfiguration configuration, string key, string fallback) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? fallback : configuration[key]!.Trim();
}
