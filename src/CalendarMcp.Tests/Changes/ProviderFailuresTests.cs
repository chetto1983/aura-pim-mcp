using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Services;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Requests;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// Aura pauses a task on a hard failure and retries a transient one. Every row here is a place
/// where naming it wrong either stops a person's watch or hides a revoked grant.
/// </summary>
[TestClass]
public class ProviderFailuresTests
{
    private static GoogleApiException Google(HttpStatusCode status, string? reason = null) => new("gmail", "failed")
    {
        HttpStatusCode = status,
        Error = reason is null ? null : new RequestError { Errors = [new SingleError { Reason = reason }] }
    };

    private static TokenResponseException TokenEndpoint(HttpStatusCode status) =>
        new(new TokenErrorResponse { Error = "invalid_grant" }, status);

    private static string? Named(Exception failure) => ProviderFailures.Classify(failure, CancellationToken.None)?.Error;

    public static IEnumerable<object[]> Failures =>
    [
        [new AccountAuthenticationRequiredException("a"), "provider_auth"],
        [new ProviderOperationException("missing clientId"), "provider_auth"],
        [new MsalUiRequiredException("invalid_grant", "revoked"), "provider_auth"],
        [new AuthenticationException("bad password"), "provider_auth"],
        [new CryptographicException("the key is not in the key ring"), "provider_auth"],
        [TokenEndpoint(HttpStatusCode.BadRequest), "provider_auth"],
        [Google(HttpStatusCode.Unauthorized), "provider_auth"],
        [new ApiException { ResponseStatusCode = 401 }, "provider_auth"],

        [Google(HttpStatusCode.Forbidden, "insufficientPermissions"), "permission_denied"],
        [Google(HttpStatusCode.Forbidden, "domainPolicy"), "permission_denied"],
        [Google(HttpStatusCode.Forbidden), "permission_denied"],
        [new ApiException { ResponseStatusCode = 403 }, "permission_denied"],

        [Google(HttpStatusCode.Forbidden, "rateLimitExceeded"), "provider_transient"],
        [Google(HttpStatusCode.Forbidden, "userRateLimitExceeded"), "provider_transient"],
        [Google(HttpStatusCode.Forbidden, "dailyLimitExceeded"), "provider_transient"],
        [Google(HttpStatusCode.TooManyRequests), "provider_transient"],
        [Google(HttpStatusCode.InternalServerError), "provider_transient"],
        [Google(HttpStatusCode.ServiceUnavailable), "provider_transient"],
        [TokenEndpoint(HttpStatusCode.ServiceUnavailable), "provider_transient"],
        [TokenEndpoint(HttpStatusCode.TooManyRequests), "provider_transient"],
        [new ApiException { ResponseStatusCode = 429 }, "provider_transient"],
        [new ApiException { ResponseStatusCode = 503 }, "provider_transient"],
        [new ApiException { ResponseStatusCode = 504 }, "provider_transient"],
        [new MsalServiceException("request_timeout", "timed out"), "provider_transient"],
        [new HttpRequestException("reset"), "provider_transient"],
        [new TimeoutException(), "provider_transient"],
        [new TaskCanceledException("HttpClient.Timeout"), "provider_transient"],
        [new IOException("reset"), "provider_transient"],
        [new SocketException(), "provider_transient"],
        [new ImapProtocolException("bye"), "provider_transient"],
        [new ServiceNotConnectedException(), "provider_transient"],
    ];

    [TestMethod]
    [DynamicData(nameof(Failures))]
    public void Failure_IsNamedForWhatAuraDoesNext(Exception failure, string expected)
    {
        Assert.AreEqual(expected, Named(failure));
    }

    [TestMethod]
    public void TheCallerGoingAway_IsNotAProviderFailure()
    {
        using var gone = new CancellationTokenSource();
        gone.Cancel();

        Assert.IsNull(ProviderFailures.Classify(new OperationCanceledException(gone.Token), gone.Token));
    }

    [TestMethod]
    public void ABug_IsNotAProviderFailure()
    {
        Assert.IsNull(Named(new NullReferenceException()));
    }

    [TestMethod]
    [DataRow(404, true)]
    [DataRow(410, true)]
    [DataRow(403, false)]
    [DataRow(500, false)]
    public void NotFound_IsReadFromTheStatus(int status, bool expected)
    {
        Assert.AreEqual(expected, ProviderFailures.IsNotFound(new ApiException { ResponseStatusCode = status }));
        Assert.AreEqual(expected, ProviderFailures.IsNotFound(Google((HttpStatusCode)status)));
    }
}
