using System.Security.Claims;
using System.Text.Encodings.Web;
using CalendarMcp.Core.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalendarMcp.Tests.Helpers;

/// <summary>
/// Stands in for the bearer's validation in an in-memory pipeline: the token's text is the subject, with the
/// tools scope granted. Everything after it (<c>AdminAuthMiddleware</c>, the tenant binding) is the real thing.
/// </summary>
internal sealed class SubjectFromToken(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logs, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logs, encoder)
{
    private const string SchemeName = "test";

    /// <summary>Makes this the pipeline's only authentication scheme, with authorization after it.</summary>
    public static void AddTo(IServiceCollection services)
    {
        services.AddAuthentication(SchemeName).AddScheme<AuthenticationSchemeOptions, SubjectFromToken>(SchemeName, _ => { });
        services.AddAuthorization();
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
            [new Claim(TenantIdentity.OAuthClaimName, header["Bearer ".Length..]), new Claim("scope", "mcp:tools")],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
