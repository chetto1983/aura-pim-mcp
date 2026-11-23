using CalendarMcp.Auth;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Security;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// «Prova di nuovo» and «Cambia password» for an IMAP account (Aura's
/// <c>docs/superpowers/specs/2026-11-20-pim-account-connect-repair-design.md</c>, amendments A3-A5): a check of the
/// account as stored, and a change of part of its provider config that is stored only once the mailbox accepts it.
/// </summary>
public static class AccountRepairEndpoints
{
    /// <summary>Aura sets it only for an identity that may name a host on a private network, and never forwards a client's copy.</summary>
    public const string AllowPrivateHostsHeader = "X-Aura-Allow-Private-Hosts";

    // A change of any of these would send the stored password to a server it was not given to (A3).
    private static readonly string[] ServerKeys = ["imapHost", "imapPort", "smtpHost", "smtpPort"];

    public static RouteGroupBuilder MapAccountRepairEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapPost("/accounts/{accountId}/verify", VerifyAccount);
        admin.MapPatch("/accounts/{accountId}/provider-config", PatchProviderConfig);
        return admin;
    }

    internal static bool IsImap(string provider) => ChangesProviders.Family(provider) == ChangesProviders.Imap;

    internal static bool AllowsPrivateHosts(HttpRequest request) =>
        string.Equals(request.Headers[AllowPrivateHostsHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The throttle, then the check: <c>Refusal</c> is the answer when either says no, otherwise the check passed,
    /// with the sending server's warning if it had one.
    /// </summary>
    internal static async Task<(IResult? Refusal, string? SmtpWarning)> CheckAsync(
        string accountId, IReadOnlyDictionary<string, string> providerConfig, bool allowPrivate,
        VerifyThrottle throttle, IImapAccountVerifier verifier, CancellationToken ct)
    {
        if (!throttle.TryEnter(accountId))
            return (VerifyResults.TooMany(), null);

        var outcome = await verifier.VerifyAsync(accountId, providerConfig, allowPrivate, ct);
        return outcome.Blocking is { } failure ? (VerifyResults.Failed(failure), null) : (null, outcome.SmtpWarning);
    }

    private static IResult NotFound(string accountId) => Results.NotFound(new { error = $"Account '{accountId}' not found." });

    private static async Task<IResult> VerifyAccount(
        string accountId,
        IAccountConfigurationService configService,
        VerifyThrottle throttle,
        IImapAccountVerifier verifier,
        CancellationToken ct)
    {
        var account = await configService.GetAccountFromConfigAsync(accountId, ct);
        if (account is null)
            return NotFound(accountId);
        if (!IsImap(account.Provider) || !ImapProviderInput.IsVerifiable(account.ProviderConfig))
            return VerifyResults.Refused("not_verifiable");

        // The check is of what the watch does (spec A8), so it is held to the account's stored permission, as every
        // connection for it is, and not to the caller's: an administrator's check must not pass where the watch is
        // refused (ledger Ruling 27). Create and PATCH use the header, because the header is what they store.
        var (refusal, smtpWarning) = await CheckAsync(
            accountId, account.ProviderConfig, ImapProviderService.AllowsPrivateHosts(account.ProviderConfig),
            throttle, verifier, ct);
        return refusal ?? Results.Ok(smtpWarning is null ? new { ok = true } : (object)new { ok = true, smtpWarning });
    }

    /// <summary>
    /// Merges the keys the request names over the stored config, checks the result, and stores it. A key absent or
    /// <c>""</c> is unchanged; a password is never trimmed, and a kept one stays the ciphertext it was. What is checked
    /// is exactly what is stored, so a typed password that starts with <c>ENC:</c> is read as typed.
    /// </summary>
    private static async Task<IResult> PatchProviderConfig(
        string accountId,
        PatchProviderConfigRequest body,
        HttpRequest request,
        IAccountConfigurationService configService,
        PasswordProtector protector,
        VerifyThrottle throttle,
        IImapAccountVerifier verifier,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var existing = await configService.GetAccountFromConfigAsync(accountId, ct);
        if (existing is null)
            return NotFound(accountId);
        if (!IsImap(existing.Provider))
            return VerifyResults.Refused("not_patchable");

        var incoming = body.ProviderConfig ?? [];
        if (ImapProviderInput.HasDuplicateKeys(incoming.Keys))
            return VerifyResults.Refused(ImapProviderInput.DuplicateKey);
        if (ImapProviderInput.NamesServerOwnedKey(incoming.Keys))
            return VerifyResults.Refused(ImapProviderInput.ServerOwnedKey);

        var changes = incoming
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var stored = existing.ProviderConfig;
        if (Changes(stored, changes, "username"))
            return VerifyResults.Refused("username_immutable");
        if (!changes.ContainsKey("password") && ServerKeys.Any(key => Changes(stored, changes, key)))
            return VerifyResults.Refused("password_required");

        var merged = new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ProviderSecrets.ProtectIncoming(changes, protector))
            merged[key] = value;
        // A member's PATCH stores false even on an account an administrator made: whoever last named the servers is
        // whose permission the account keeps.
        var allowPrivate = AllowsPrivateHosts(request);
        var (next, inputError) = ImapProviderInput.ForWrite(merged, allowPrivate);
        if (next is null)
            return VerifyResults.Refused(inputError!);
        var (valid, validationError) = TenantProviderConfig.Validate(existing.Provider, next);
        if (!valid)
            return Results.BadRequest(new { error = validationError });

        var (refusal, smtpWarning) = await CheckAsync(accountId, next, allowPrivate, throttle, verifier, ct);
        if (refusal is not null)
            return refusal;

        try
        {
            await configService.MergeProviderConfigAsync(accountId, stored, next, ct);
        }
        catch (ConcurrencyException)
        {
            return Results.Conflict(new { error = "changed" });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(accountId);
        }

        AccountConfigurationReload.Apply(configuration);
        return Results.Ok(AdminEndpoints.AccountBody(existing, smtpWarning));
    }

    // A value the request names and the store does not hold as it is: absent there counts as different.
    private static bool Changes(IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string> changes, string key) =>
        changes.TryGetValue(key, out var value) &&
        !(stored.TryGetValue(key, out var current) && string.Equals(current, value, StringComparison.Ordinal));
}
