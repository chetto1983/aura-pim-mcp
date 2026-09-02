using System.Security.Claims;

namespace CalendarMcp.HttpServer.Security;

/// <summary>
/// Resolves the tenant an authenticated caller may reach, once, at authentication time.
/// <para>
/// Which tenant a caller gets is a property of (issuer, subject) together, not of the
/// subject alone — see <see cref="McpOAuthOptions.TenantIdentityFor"/>. Doing that here
/// rather than at every read means <c>TenantIdentity.FromPrincipal</c> and its two callers
/// stay exactly as they were: they still read one <c>sub</c> claim, and it is now the
/// resolved one. A resolution spread across call sites is a resolution that will one day
/// be missing from one of them.
/// </para>
/// </summary>
internal static class McpTenantClaims
{
    private const string SubjectClaim = "sub";
    private const string IssuerClaim = "iss";

    /// <summary>Provenance: the subject as the issuer actually asserted it, kept because a
    /// derived GUID says nothing on its own to anyone auditing "which account is this".</summary>
    internal const string OriginalSubjectClaim = "oauth_sub";

    internal static ClaimsPrincipal? Rebind(ClaimsPrincipal? principal, McpOAuthOptions oauth)
    {
        var subject = principal?.FindFirst(SubjectClaim)?.Value;
        if (principal is null || string.IsNullOrWhiteSpace(subject))
        {
            return principal;
        }
        var issuer = principal.FindFirst(IssuerClaim)?.Value;
        var tenant = oauth.TenantIdentityFor(issuer, subject);
        if (string.Equals(tenant, subject, StringComparison.Ordinal))
        {
            return principal;
        }

        var claims = principal.Claims.Where(claim => claim.Type != SubjectClaim).ToList();
        claims.Add(new Claim(SubjectClaim, tenant));
        claims.Add(new Claim(OriginalSubjectClaim, subject));
        var identity = new ClaimsIdentity(
            claims,
            principal.Identity?.AuthenticationType,
            SubjectClaim,
            ClaimsIdentity.DefaultRoleClaimType);
        return new ClaimsPrincipal(identity);
    }
}
