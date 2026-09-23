using System.Collections.Concurrent;
using CalendarMcp.Core.Configuration;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// Manages server-side Google OAuth authorization code flow for the admin UI.
/// Handles generating consent URLs, exchanging authorization codes for tokens,
/// and storing tokens using the same FileDataStore paths as the CLI.
/// </summary>
public class GoogleOAuthManager
{
    private readonly ILogger<GoogleOAuthManager> _logger;
    private readonly ConcurrentDictionary<string, PendingOAuthState> _pendingStates = new();

    private static readonly string[] Scopes = CalendarMcp.Core.Constants.GoogleScopes.Default;

    public GoogleOAuthManager(ILogger<GoogleOAuthManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generate a Google OAuth consent URL whose redirect goes through the shared relay
    /// (<paramref name="redirectUri"/>) back to <paramref name="installCallbackUrl"/>, and remember
    /// the state and PKCE verifier for the exchange.
    /// </summary>
    public string GetAuthorizationUrl(string accountId, string clientId, string clientSecret, string redirectUri, string installCallbackUrl)
    {
        using var flow = CreateFlow(clientId, clientSecret);

        var state = GoogleOAuthRelay.CreateState(installCallbackUrl);
        var codeVerifier = GoogleOAuthRelay.CreateCodeVerifier();
        _pendingStates[state] = new PendingOAuthState
        {
            AccountId = accountId,
            ClientId = clientId,
            ClientSecret = clientSecret,
            RedirectUri = redirectUri,
            CodeVerifier = codeVerifier,
            CreatedAt = DateTimeOffset.UtcNow
        };

        CleanupExpiredStates();

        var authUrl = (GoogleAuthorizationCodeRequestUrl)flow.CreateAuthorizationCodeRequest(redirectUri);
        authUrl.State = state;
        authUrl.Scope = string.Join(" ", Scopes);
        // Offline access plus a forced consent screen is what makes Google return a refresh token.
        authUrl.AccessType = "offline";
        authUrl.Prompt = "consent";
        authUrl.CodeChallenge = GoogleOAuthRelay.CodeChallenge(codeVerifier);
        authUrl.CodeChallengeMethod = "S256";

        return authUrl.Build().AbsoluteUri;
    }

    /// <summary>
    /// Exchange an authorization code for tokens and store them. The state must be exactly one
    /// this server issued; its redirect URI and PKCE verifier are replayed from that record.
    /// </summary>
    public async Task<string> ExchangeCodeAsync(string state, string code, CancellationToken cancellationToken)
    {
        if (!_pendingStates.TryRemove(state, out var pending))
        {
            throw new InvalidOperationException("Invalid or expired OAuth state parameter.");
        }

        // Check expiry (10 minutes)
        if (DateTimeOffset.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException("OAuth state has expired. Please try again.");
        }

        using var flow = CreateFlow(pending.ClientId, pending.ClientSecret);

        _logger.LogInformation("Exchanging authorization code for tokens for account {AccountId}", pending.AccountId);

        // The flow's own code-exchange overload that takes a code verifier is protected internal
        // in Google.Apis.Auth 1.76.0, so the token request is built and executed directly.
        var tokenRequest = new AuthorizationCodeTokenRequest
        {
            ClientId = pending.ClientId,
            ClientSecret = pending.ClientSecret,
            Code = code,
            RedirectUri = pending.RedirectUri,
            CodeVerifier = pending.CodeVerifier,
        };
        var tokenResponse = await tokenRequest.ExecuteAsync(flow.HttpClient, flow.TokenServerUrl, cancellationToken, flow.Clock);

        // Store the token using FileDataStore at the same path the CLI uses.
        // FileDataStore names files as "{TypeFullName}-{key}", so key "user" produces
        // "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user" — matching the CLI.
        var credPath = ConfigurationPaths.GetGoogleCredentialsDirectory(pending.AccountId);
        var dataStore = new FileDataStore(credPath, true);
        await dataStore.StoreAsync("user", tokenResponse);

        _logger.LogInformation("Google OAuth tokens stored for account {AccountId}", pending.AccountId);
        return pending.AccountId;
    }

    private static GoogleAuthorizationCodeFlow CreateFlow(string clientId, string clientSecret)
    {
        return new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            },
            Scopes = Scopes
        });
    }

    private void CleanupExpiredStates()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var kvp in _pendingStates)
        {
            if (kvp.Value.CreatedAt < cutoff)
            {
                _pendingStates.TryRemove(kvp.Key, out _);
            }
        }
    }

    private class PendingOAuthState
    {
        public required string AccountId { get; init; }
        public required string ClientId { get; init; }
        public required string ClientSecret { get; init; }
        public required string RedirectUri { get; init; }
        public required string CodeVerifier { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
