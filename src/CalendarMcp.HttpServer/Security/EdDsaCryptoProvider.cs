using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSec.Cryptography;

namespace CalendarMcp.HttpServer.Security;

/// <summary>
/// Adds RFC 8037 Ed25519 verification to IdentityModel without replacing its
/// issuer, audience, lifetime, or signing-key validation.
/// </summary>
internal sealed class EdDsaCryptoProvider : ICryptoProvider
{
    private const string Algorithm = "EdDSA";

    public bool IsSupportedAlgorithm(string algorithm, params object[] args) =>
        string.Equals(algorithm, Algorithm, StringComparison.Ordinal) &&
        args.Length >= 1 &&
        args[0] is JsonWebKey key &&
        IsEd25519Key(key) &&
        (args.Length == 1 || args[1] is false);

    public object Create(string algorithm, params object[] args)
    {
        if (!IsSupportedAlgorithm(algorithm, args) || args[0] is not JsonWebKey key)
            throw new NotSupportedException("Only Ed25519 signature verification is supported.");

        return new EdDsaSignatureProvider(key);
    }

    public void Release(object cryptoInstance)
    {
        if (cryptoInstance is IDisposable disposable)
            disposable.Dispose();
    }

    internal static bool IsEd25519Key(JsonWebKey key) =>
        string.Equals(key.Kty, "OKP", StringComparison.Ordinal) &&
        string.Equals(key.Crv, "Ed25519", StringComparison.Ordinal) &&
        string.Equals(key.Alg, Algorithm, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(key.X);

    private sealed class EdDsaSignatureProvider : SignatureProvider
    {
        private readonly PublicKey _publicKey;

        internal EdDsaSignatureProvider(JsonWebKey key) : base(key, EdDsaCryptoProvider.Algorithm)
        {
            var rawPublicKey = Base64UrlEncoder.DecodeBytes(key.X);
            _publicKey = PublicKey.Import(
                SignatureAlgorithm.Ed25519,
                rawPublicKey,
                KeyBlobFormat.RawPublicKey);
        }

        public override byte[] Sign(byte[] input) =>
            throw new NotSupportedException("The MCP resource server does not sign tokens.");

        public override bool Verify(byte[] input, byte[] signature) =>
            SignatureAlgorithm.Ed25519.Verify(_publicKey, input, signature);

        public override bool Verify(
            byte[] input,
            int inputOffset,
            int inputLength,
            byte[] signature,
            int signatureOffset,
            int signatureLength) =>
            SignatureAlgorithm.Ed25519.Verify(
                _publicKey,
                input.AsSpan(inputOffset, inputLength),
                signature.AsSpan(signatureOffset, signatureLength));

        protected override void Dispose(bool disposing) { }
    }
}

/// <summary>
/// IdentityModel drops OKP keys when converting a JWKS to built-in SecurityKey
/// types. Preserve those raw keys while retaining every built-in signing key.
/// </summary>
internal static class EdDsaSigningKeys
{
    internal static IEnumerable<SecurityKey> Resolve(string? keyId, BaseConfiguration configuration)
    {
        var builtIn = configuration.SigningKeys.Where(key => MatchesKeyId(key, keyId));
        if (configuration is not OpenIdConnectConfiguration openId || openId.JsonWebKeySet is null)
            return builtIn;

        var edDsa = openId.JsonWebKeySet.Keys.Where(key =>
            MatchesKeyId(key, keyId) &&
            EdDsaCryptoProvider.IsEd25519Key(key) &&
            (string.IsNullOrEmpty(key.Use) || string.Equals(key.Use, "sig", StringComparison.Ordinal)));
        return builtIn.Concat(edDsa);
    }

    private static bool MatchesKeyId(SecurityKey key, string? keyId) =>
        string.IsNullOrEmpty(keyId) || string.Equals(key.KeyId, keyId, StringComparison.Ordinal);
}
