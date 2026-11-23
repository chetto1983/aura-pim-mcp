using System.Net.Sockets;
using System.Security.Cryptography;
using CalendarMcp.Core.Security;
using CalendarMcp.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using static CalendarMcp.Core.Providers.ImapProviderService;

namespace CalendarMcp.Core.Providers;

/// <summary>Why a check failed: <c>Step</c> is <c>imap</c> or <c>smtp</c>, <c>Reason</c> one of <see cref="VerifyReasons"/>.</summary>
public sealed record VerifyFailure(string Step, string Reason);

/// <summary>
/// What a check found. <c>Blocking</c> keeps the account from being saved. <c>SmtpWarning</c> is a sending server
/// that could not be reached or refused TLS: the watch only reads, so that is stored and said, not refused.
/// </summary>
public sealed record VerifyOutcome(VerifyFailure? Blocking, string? SmtpWarning);

/// <summary>Checks that an IMAP account's mailbox answers the way the mail watch will read it.</summary>
public interface IImapAccountVerifier
{
    /// <summary>
    /// Logs in to the IMAP server and runs the watch's first read, then connects and logs in to SMTP. Private
    /// addresses are connected only when the config's <c>allowPrivateHosts</c> allows them, as for every connection
    /// the account makes (<see cref="ImapProviderService.AllowsPrivateHosts"/>). A cancelled <paramref name="ct"/> throws.
    /// </summary>
    /// <remarks>
    /// The caller validates <paramref name="providerConfig"/> first (the admin API's <c>ImapProviderInput</c>): a
    /// username and a password, host names a resolver takes, ports in 1-65535, and <c>imapSecurity</c> and
    /// <c>smtpSecurity</c> absent or one of <c>ssl</c>, <c>starttls</c>, <c>none</c>. Past that, every failure is a
    /// <see cref="VerifyOutcome"/>; what is thrown on bad input names a bug in the caller, not a mail server.
    /// </remarks>
    Task<VerifyOutcome> VerifyAsync(
        string accountId, IReadOnlyDictionary<string, string> providerConfig, CancellationToken ct);
}

/// <summary>
/// The only place a verification's exception becomes a reason. The stage (<c>connect</c>, <c>auth</c>, <c>read</c>)
/// is where it was thrown: a dropped connection while logging in means the login was refused, while reading it
/// means the server went away.
/// </summary>
public static class VerifyReasons
{
    public const string AuthFailed = "auth_failed";
    public const string Unreachable = "unreachable";
    public const string TlsFailed = "tls_failed";
    public const string MailboxRefused = "mailbox_refused";
    public const string SmtpAuthFailed = "smtp_auth_failed";
    public const string SmtpUnreachable = "smtp_unreachable";

    public const string Imap = "imap";
    public const string Smtp = "smtp";

    public const string Connect = "connect";
    public const string Auth = "auth";
    public const string Read = "read";

    private const int MaxServerLine = 200;

    // MailKit's own text for a server that advertises LOGINDISABLED (ImapClient.cs#L1211-1213 at 4.18.1): the
    // server refuses a password in clear, which is a TLS problem, not a wrong password.
    private const string LoginDisabled = "The LOGIN command is disabled.";

    /// <exception cref="InvalidOperationException">The exception names no failure of a mail server: a bug.</exception>
    public static VerifyFailure From(string step, string stage, Exception failure) =>
        new(step, step == Smtp ? ForSmtp(stage, failure) : ForImap(stage, failure));

    private static string ForImap(string stage, Exception failure) => failure switch
    {
        CryptographicException => AuthFailed,
        AuthenticationException { Message: LoginDisabled } => TlsFailed,
        AuthenticationException => AuthFailed,
        SslHandshakeException => TlsFailed,
        NotSupportedException when stage == Connect => TlsFailed,
        NotSupportedException or ImapProtocolException when stage == Auth => AuthFailed,
        _ when IsUnpreparable(stage, failure) => AuthFailed,
        ImapCommandException or FolderNotFoundException or ProviderOperationException when stage == Read => MailboxRefused,
        _ when IsUnreachable(failure) => Unreachable,
        _ => throw Unnamed(failure)
    };

    private static string ForSmtp(string stage, Exception failure) => failure switch
    {
        SslHandshakeException => TlsFailed,
        NotSupportedException when stage == Connect => TlsFailed,
        AuthenticationException { InnerException: SmtpCommandException refusal } => ByStatus(refusal.StatusCode),
        SmtpCommandException refusal when stage == Auth => ByStatus(refusal.StatusCode),
        AuthenticationException or SaslException or NotSupportedException when stage == Auth => SmtpAuthFailed,
        _ when IsUnpreparable(stage, failure) => SmtpAuthFailed,
        _ when IsUnreachable(failure) => SmtpUnreachable,
        _ => throw Unnamed(failure)
    };

    // SCRAM prepares the credentials with SASLprep (RFC 4013), and MailKit's SaslMechanism.SaslPrep throws a plain
    // ArgumentException for a control or prohibited character (MailKit 4.18.1 SaslMechanism.cs#L724-745), such as a
    // pasted newline: the person retypes the password. A null argument stays a bug.
    private static bool IsUnpreparable(string stage, Exception failure) =>
        stage == Auth && failure is ArgumentException and not ArgumentNullException;

    // A 4xx on AUTH is the server's own trouble (454: temporary authentication failure, RFC 4954 section 6).
    private static string ByStatus(SmtpStatusCode status) => (int)status is >= 400 and < 500 ? SmtpUnreachable : SmtpAuthFailed;

    // PrivateAddressException is an IOException: a host a member may not reach reads as one that cannot be reached.
    // For SMTP the verifier tells them apart, because only a server that cannot be reached is a warning.
    private static bool IsUnreachable(Exception failure) => failure is SocketException or IOException or TimeoutException
        or OperationCanceledException or ProtocolException or CommandException or ServiceNotConnectedException;

    /// <summary>A step that ran out of its own budget: whatever the client threw when it was cut short.</summary>
    internal static VerifyFailure OutOfTime(string step) => new(step, step == Smtp ? SmtpUnreachable : Unreachable);

    private static InvalidOperationException Unnamed(Exception failure) =>
        new($"A verification failed with {failure.GetType().Name}, which names no mail server failure.", failure);

    /// <summary>
    /// The server's own words, for the log only and never for a response: the line a refusal carried, on one line
    /// and cut short, so a server cannot write log lines of its own. A refused private address has no server line:
    /// it names the host and the address it resolved to instead.
    /// </summary>
    internal static string ServerLine(Exception failure)
    {
        var line = failure switch
        {
            ImapCommandException imap => imap.ResponseText,
            ImapProtocolException protocol => protocol.Message,
            SmtpCommandException smtp => $"{(int)smtp.StatusCode} {smtp.Message}",
            AuthenticationException { InnerException: SmtpCommandException smtp } => $"{(int)smtp.StatusCode} {smtp.Message}",
            AuthenticationException auth => auth.Message,
            PrivateAddressException refused => refused.Message,
            _ => string.Empty
        } ?? string.Empty;

        var flat = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length > MaxServerLine ? flat[..MaxServerLine] : flat;
    }
}

/// <summary>
/// Two steps, each with a budget of its own. IMAP: a guarded connection, the login, and the watch's first read of
/// the configured inbox (<see cref="ImapProviderService.FirstReadAsync"/>). SMTP: a guarded connection and the
/// login. Every connection is the verifier's own, never the provider's pooled one.
/// </summary>
internal sealed class ImapAccountVerifier(HostGuard guard, PasswordProtector protector, ILogger<ImapAccountVerifier> logger)
    : IImapAccountVerifier
{
    internal TimeSpan ImapBudget { get; set; } = TimeSpan.FromSeconds(12);

    internal TimeSpan SmtpBudget { get; set; } = TimeSpan.FromSeconds(8);

    public async Task<VerifyOutcome> VerifyAsync(
        string accountId, IReadOnlyDictionary<string, string> providerConfig, CancellationToken ct)
    {
        ImapAccountConfig cfg;
        try
        {
            cfg = ImapAccountConfig.From(accountId, providerConfig, protector);
        }
        catch (CryptographicException e)
        {
            return new VerifyOutcome(Failed(accountId, VerifyReasons.Imap, VerifyReasons.Auth, e), null);
        }

        var imap = await StepAsync(accountId, VerifyReasons.Imap, ImapBudget, (stage, token) => ImapAsync(cfg, stage, token), ct);
        if (imap is not null)
            return new VerifyOutcome(imap, null);

        // A warning is for an SMTP server that does not answer. A host this account may not reach blocks like a
        // refused login: every send is held to the same permission, so none could ever reach it.
        var refused = false;
        var smtp = await StepAsync(accountId, VerifyReasons.Smtp, SmtpBudget, async (stage, token) =>
        {
            try
            {
                return await SmtpAsync(cfg, stage, token);
            }
            catch (PrivateAddressException)
            {
                refused = true;
                throw;
            }
        }, ct);
        return refused || smtp is null or { Reason: VerifyReasons.SmtpAuthFailed }
            ? new VerifyOutcome(smtp, null)
            : new VerifyOutcome(null, smtp.Reason);
    }

    private async Task<VerifyFailure?> ImapAsync(ImapAccountConfig cfg, Stage stage, CancellationToken ct)
    {
        using var client = await ImapConnections.ConnectImapAsync(guard, cfg, ct);

        stage.Current = VerifyReasons.Auth;
        await ImapConnections.LogInAsync(client, cfg, ct);

        stage.Current = VerifyReasons.Read;
        await FirstReadAsync(client, cfg, ct);

        await LogOutAsync(client, ct);
        return null;
    }

    private async Task<VerifyFailure?> SmtpAsync(ImapAccountConfig cfg, Stage stage, CancellationToken ct)
    {
        using var client = await ImapConnections.ConnectSmtpAsync(guard, cfg, ct);

        stage.Current = VerifyReasons.Auth;
        await ImapConnections.LogInAsync(client, cfg, ct);
        var authenticated = client.IsAuthenticated;
        await LogOutAsync(client, ct);
        if (authenticated)
            return null;

        // The account has a password and the server never asked for it: mail would go out only if the server
        // relays it unchecked, which is not this account sending.
        logger.LogWarning("Verifying account {AccountId} failed at {Step}/{Stage}: the server offers no AUTH",
            cfg.AccountId, VerifyReasons.Smtp, VerifyReasons.Auth);
        return new VerifyFailure(VerifyReasons.Smtp, VerifyReasons.SmtpAuthFailed);
    }

    private async Task<VerifyFailure?> StepAsync(
        string accountId, string step, TimeSpan budget,
        Func<Stage, CancellationToken, Task<VerifyFailure?>> run, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(budget);
        var stage = new Stage();

        try
        {
            return await run(stage, timer.Token);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Once the step's own budget fired, what MailKit throws says where the cut landed, not what the server did.
            return Failed(accountId, step, stage.Current, e, outOfTime: timer.IsCancellationRequested);
        }
    }

    // The exception itself is never logged: its message and stack can carry what the provider said about the
    // account. Its type and the server's line are what the next field case needs (spec amendment A8).
    private VerifyFailure Failed(string accountId, string step, string stage, Exception e, bool outOfTime = false)
    {
        logger.LogWarning("Verifying account {AccountId} failed at {Step}/{Stage}: {Failure} {ServerLine}",
            accountId, step, stage, e.GetType().Name, VerifyReasons.ServerLine(e));
        return outOfTime ? VerifyReasons.OutOfTime(step) : VerifyReasons.From(step, stage, e);
    }

    // The check has passed by now: a server that drops the LOGOUT has still answered everything asked of it.
    private async Task LogOutAsync(IMailService client, CancellationToken ct)
    {
        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception e)
        {
            logger.LogDebug("LOGOUT after a passing verification failed: {Failure}", e.GetType().Name);
        }
    }

    private sealed class Stage
    {
        public string Current { get; set; } = VerifyReasons.Connect;
    }
}
