using System.Net;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Tests.Helpers;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// Whether a person must sign in again is decided by what the token endpoint answers, so these run
/// Google's own credential and flow, with only the HTTP under them scripted. A 429 or a 5xx says
/// nothing about the grant, and reading one as revoked would stop an account until someone signs in.
/// </summary>
[TestClass]
public class GoogleTokenRefreshTests
{
    private const string Account = "google-account";

    private readonly FakeHttp _http = new();
    private string _directory = string.Empty;
    private string? _previousConfig;

    [TestInitialize]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("google-token-").FullName;
        _previousConfig = Environment.GetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable);
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _directory);

        var tokenFile = ConfigurationPaths.GetGoogleTokenFilePath(Account);
        Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
        File.WriteAllText(tokenFile, "{}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _previousConfig);
        Directory.Delete(_directory, recursive: true);
    }

    private static SingleAccountRegistry Registry() =>
        new(TestData.CreateAccount(id: Account, provider: "google", providerConfig: new Dictionary<string, string>
        {
            ["clientId"] = "client",
            ["clientSecret"] = "secret"
        }));

    private UserCredential Stale(ClientSecrets secrets, string? refreshToken) => new(
        new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = ["scope"],
            HttpClientFactory = new FakeGoogleTransport(_http).HttpClientFactory,
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None
        }),
        "user",
        new TokenResponse
        {
            AccessToken = "old",
            RefreshToken = refreshToken,
            ExpiresInSeconds = 3600,
            IssuedUtc = DateTime.UtcNow.AddHours(-2)
        });

    private Task AskAsync(string? refreshToken = "refresh") =>
        new GoogleProviderService(NullLogger<GoogleProviderService>.Instance, Registry())
        {
            Authorize = (secrets, _, _) => Task.FromResult(Stale(secrets, refreshToken))
        }.GetCalendarEventDetailsAsync(Account, "primary", "ev");

    private void TokenEndpointAnswers(HttpStatusCode status) =>
        _http.On("/token", """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", status);

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task ARefusedGrant_AsksForASignIn(HttpStatusCode status)
    {
        TokenEndpointAnswers(status);

        var failure = await Assert.ThrowsExactlyAsync<AccountAuthenticationRequiredException>(() => AskAsync());

        Assert.IsInstanceOfType<TokenResponseException>(failure.InnerException);
        Assert.AreEqual(1, _http.CountOf("/token"));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task ATokenEndpointInTrouble_SaysNothingAboutTheGrant(HttpStatusCode status)
    {
        TokenEndpointAnswers(status);

        var failure = await Assert.ThrowsExactlyAsync<TokenResponseException>(() => AskAsync());

        Assert.AreEqual(status, failure.StatusCode);
    }

    [TestMethod]
    public async Task ATokenFailureWithNoStatus_AsksForASignIn()
    {
        var google = new GoogleProviderService(NullLogger<GoogleProviderService>.Instance, Registry())
        {
            Authorize = (_, _, _) => throw new TokenResponseException(new TokenErrorResponse { Error = "invalid_grant" })
        };

        await Assert.ThrowsExactlyAsync<AccountAuthenticationRequiredException>(
            () => google.GetCalendarEventDetailsAsync(Account, "primary", "ev"));
    }

    [TestMethod]
    public async Task AStaleTokenThatCannotRefresh_AsksForASignIn()
    {
        await Assert.ThrowsExactlyAsync<AccountAuthenticationRequiredException>(() => AskAsync(refreshToken: null));

        Assert.AreEqual(0, _http.CountOf("/token"));
    }
}
