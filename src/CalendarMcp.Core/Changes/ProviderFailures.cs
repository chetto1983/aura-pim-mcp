using System.Net;
using System.Net.Sockets;
using CalendarMcp.Core.Services;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;

namespace CalendarMcp.Core.Changes;

/// <summary>
/// The one place that turns a provider's exception into the contract's failure. Aura pauses a task
/// on a hard failure and retries a transient one on its next tick, so a blip named as a revoked
/// grant stops a person's watch, and a revoked grant named as a blip hides for an hour.
/// </summary>
internal static class ProviderFailures
{
    // https://developers.google.com/workspace/gmail/api/guides/handle-errors: these 403s are limits
    // to wait out. Any other 403 means the account may not do this.
    private static readonly HashSet<string> GoogleRateReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "rateLimitExceeded", "userRateLimitExceeded", "dailyLimitExceeded", "quotaExceeded"
    };

    /// <summary>The contract's failure for <paramref name="failure"/>, or null when it is no provider failure.</summary>
    public static ChangesException? Classify(Exception failure, CancellationToken cancellationToken) => failure switch
    {
        ChangesException => null,
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        CursorExpiredException => null,

        AccountAuthenticationRequiredException => ChangesException.ProviderAuth(failure),
        ProviderOperationException => ChangesException.ProviderAuth(failure),
        MsalUiRequiredException => ChangesException.ProviderAuth(failure),
        AuthenticationException => ChangesException.ProviderAuth(failure),

        TokenResponseException token => FromStatus(failure, (int?)token.StatusCode),
        GoogleApiException google => FromGoogle(google),
        ApiException graph => FromStatus(failure, graph.ResponseStatusCode),

        MsalException or HttpRequestException or TimeoutException or OperationCanceledException
            or IOException or SocketException or ImapProtocolException or ImapCommandException
            or ServiceNotConnectedException or ServiceNotAuthenticatedException or SslHandshakeException
            => ChangesException.ProviderTransient(failure),

        _ => null
    };

    /// <summary>Whether <paramref name="failure"/> says the item is not there, as opposed to unreadable.</summary>
    public static bool IsNotFound(Exception failure) => failure switch
    {
        GoogleApiException google => google.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone,
        ApiException graph => graph.ResponseStatusCode is 404 or 410,
        _ => false
    };

    private static ChangesException FromGoogle(GoogleApiException failure)
    {
        var reason = failure.Error?.Errors?.FirstOrDefault()?.Reason;
        if (failure.HttpStatusCode == HttpStatusCode.Forbidden && reason is not null && GoogleRateReasons.Contains(reason))
            return ChangesException.ProviderTransient(failure);

        return FromStatus(failure, (int)failure.HttpStatusCode);
    }

    // Everything else a provider answers is not a statement about the grant, so it retries. A 400
    // that never clears surfaces through Aura's one alarm for an account failing for an hour.
    private static ChangesException FromStatus(Exception failure, int? status) => status switch
    {
        401 => ChangesException.ProviderAuth(failure),
        403 => ChangesException.PermissionDenied(failure),
        _ when failure is TokenResponseException && status is >= 400 and < 500 and not 429 =>
            ChangesException.ProviderAuth(failure),
        _ => ChangesException.ProviderTransient(failure)
    };
}
