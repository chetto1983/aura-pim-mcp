using CalendarMcp.HttpServer.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSec.Cryptography;
using System.Text;

namespace CalendarMcp.Tests.Security;

[TestClass]
public sealed class EdDsaCryptoProviderTests
{
    [TestMethod]
    public void Verify_AcceptsValidEd25519SignatureAndRejectsTampering()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var privateKey = Key.Create(algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        var message = "calendar-mcp"u8.ToArray();
        var signature = algorithm.Sign(privateKey, message);
        var key = JsonWebKey(privateKey.PublicKey.Export(KeyBlobFormat.RawPublicKey), "test-key");

        var cryptoProvider = new EdDsaCryptoProvider();
        using var verifier = (SignatureProvider)cryptoProvider.Create("EdDSA", key, false);

        Assert.IsTrue(verifier.Verify(message, signature));

        var paddedMessage = new byte[message.Length + 4];
        message.CopyTo(paddedMessage, 2);
        var paddedSignature = new byte[signature.Length + 6];
        signature.CopyTo(paddedSignature, 3);
        Assert.IsTrue(verifier.Verify(
            paddedMessage, 2, message.Length,
            paddedSignature, 3, signature.Length));

        message[0] ^= 1;
        Assert.IsFalse(verifier.Verify(message, signature));
    }

    [TestMethod]
    public void Resolve_ReturnsRawOkpKeyDiscardedByDefaultIdentityModelConversion()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var privateKey = Key.Create(algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        var key = JsonWebKey(privateKey.PublicKey.Export(KeyBlobFormat.RawPublicKey), "test-key");
        var configuration = new OpenIdConnectConfiguration
        {
            JsonWebKeySet = new JsonWebKeySet($$"""
                {"keys":[{"kty":"{{key.Kty}}","crv":"{{key.Crv}}","x":"{{key.X}}","kid":"{{key.Kid}}","alg":"{{key.Alg}}","use":"sig"}]}
                """)
        };

        var resolved = EdDsaSigningKeys.Resolve("test-key", configuration).ToArray();

        Assert.HasCount(1, resolved);
        Assert.IsInstanceOfType<JsonWebKey>(resolved[0]);
        Assert.AreEqual("OKP", ((JsonWebKey)resolved[0]).Kty);
    }

    [TestMethod]
    public async Task JsonWebTokenHandler_ValidatesEd25519CompactJws()
    {
        const string issuer = "http://127.0.0.1:9080";
        const string audience = "http://127.0.0.1:8093/";
        var algorithm = SignatureAlgorithm.Ed25519;
        using var privateKey = Key.Create(algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        var key = JsonWebKey(privateKey.PublicKey.Export(KeyBlobFormat.RawPublicKey), "test-key");
        var header = Base64UrlEncoder.Encode("""{"alg":"EdDSA","kid":"test-key","typ":"JWT"}""");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var payload = Base64UrlEncoder.Encode($$"""{"iss":"{{issuer}}","aud":"{{audience}}","sub":"test-tenant","exp":{{expiresAt}}}""");
        var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var signature = Base64UrlEncoder.Encode(algorithm.Sign(privateKey, signingInput));
        var token = $"{header}.{payload}.{signature}";
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = audience,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            CryptoProviderFactory = new CryptoProviderFactory
            {
                CustomCryptoProvider = new EdDsaCryptoProvider()
            }
        });

        Assert.IsTrue(validation.IsValid, validation.Exception?.ToString());
    }

    private static JsonWebKey JsonWebKey(byte[] publicKey, string keyId) => new()
    {
        Kty = "OKP",
        Crv = "Ed25519",
        X = Base64UrlEncoder.Encode(publicKey),
        Kid = keyId,
        Alg = "EdDSA",
        Use = "sig"
    };
}
