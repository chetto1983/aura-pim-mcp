using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CalendarMcp.HttpServer.Security;

/// <summary>
/// Signing keys for every trusted authorization server.
/// <para>
/// A single JwtBearer handler discovers exactly one metadata document — the home issuer's.
/// Once more than one issuer is trusted, the keys for the others have to come from
/// somewhere, and they must NOT come from a shared pool: one issuer's tokens verified
/// against another's keys is precisely the confusion the trusted list exists to prevent.
/// So each issuer keeps its own configuration manager, and resolution is keyed on the
/// issuer the token actually claims.
/// </para>
/// </summary>
internal sealed class TrustedIssuerKeys
{
    private readonly string _homeIssuer;
    private readonly Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _foreign;

    internal TrustedIssuerKeys(McpOAuthOptions oauth)
    {
        _homeIssuer = oauth.Home.Issuer;
        _foreign = oauth.Issuers
            .Skip(1)
            .ToDictionary(
                issuer => issuer.Issuer,
                issuer => new ConfigurationManager<OpenIdConnectConfiguration>(
                    issuer.MetadataAddress,
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever { RequireHttps = issuer.MetadataAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase) }),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves the keys that may have signed <paramref name="issuer"/>'s token.
    /// <paramref name="homeConfiguration"/> is the document the handler already discovered,
    /// so the home issuer costs no extra fetch — the overwhelmingly common path.
    /// An issuer that is not trusted resolves to no keys at all, which fails validation.
    /// </summary>
    internal IEnumerable<SecurityKey> Resolve(string? keyId, string? issuer, BaseConfiguration? homeConfiguration)
    {
        var claimed = issuer?.Trim().TrimEnd('/');
        if (string.Equals(claimed, _homeIssuer, StringComparison.Ordinal))
        {
            return homeConfiguration is null ? [] : EdDsaSigningKeys.Resolve(keyId, homeConfiguration);
        }
        if (claimed is null || !_foreign.TryGetValue(claimed, out var manager))
        {
            return [];
        }
        // Blocking on a cached document. The resolver hook is synchronous, and after the
        // first request per issuer this returns from the manager's cache; the alternative
        // is a second authentication scheme per issuer, which buys nothing here.
        var configuration = manager.GetConfigurationAsync(CancellationToken.None).GetAwaiter().GetResult();
        return EdDsaSigningKeys.Resolve(keyId, configuration);
    }
}
