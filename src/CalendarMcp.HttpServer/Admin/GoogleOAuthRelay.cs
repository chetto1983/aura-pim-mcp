using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// Google always returns to one fixed relay URI; this install's own callback travels inside
/// <c>state</c> as <c>&lt;nonce&gt;.&lt;base64url(callback)&gt;</c> so the relay can forward the
/// browser back. The relay page lives in github.com/chetto1983/aura-connect and only forwards to
/// <see cref="CallbackPath"/>. The whole state is matched against the one this install issued
/// before a code is exchanged, so a state rewritten in transit is refused here.
/// </summary>
internal static class GoogleOAuthRelay
{
    public const string CallbackPath = "/admin/auth/google/callback";

    public static string CreateState(string installCallbackUrl) =>
        $"{Guid.NewGuid():N}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(installCallbackUrl))}";

    // RFC 7636 §4.1: 32 random bytes encode to a 43-character verifier, the minimum length.
    public static string CreateCodeVerifier() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string CodeChallenge(string codeVerifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    /// <summary>
    /// The callback URL a browser can reach this install at: an explicit <paramref name="returnBase"/>
    /// (the origin the operator's browser is on), else the configured external base, else the
    /// request's own forwarded scheme and host.
    /// </summary>
    public static string InstallCallbackUrl(string? returnBase, string? externalBaseUrl, HttpRequest request)
    {
        if (!string.IsNullOrEmpty(returnBase))
        {
            return OriginOf(returnBase) + CallbackPath;
        }
        if (!string.IsNullOrEmpty(externalBaseUrl))
        {
            return externalBaseUrl.TrimEnd('/') + CallbackPath;
        }
        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
        var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;
        return $"{scheme}://{host}{CallbackPath}";
    }

    /// <summary>
    /// Accepts only a bare http(s) origin, so a caller cannot smuggle a path, query or credentials
    /// into the callback the relay will forward the browser to.
    /// </summary>
    public static string OriginOf(string returnBase)
    {
        if (!Uri.TryCreate(returnBase, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/")
        {
            throw new ArgumentException("returnBase must be a bare http(s) origin such as https://aura.example.com.", nameof(returnBase));
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
