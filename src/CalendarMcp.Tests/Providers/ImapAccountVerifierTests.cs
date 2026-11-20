using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Security;
using CalendarMcp.Tests.Changes;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// «Prova di nuovo» must say what the watch will meet: a refused password, a server without TLS, an inbox the
/// watch cannot read. Each reason here is what the cockpit tells a person, so a wrong one sends them to the
/// wrong fix. The integration cases run against the CI servers, in clear (<c>none</c>) unless they say otherwise.
/// </summary>
[TestClass]
public class ImapAccountVerifierTests
{
    private const string AccountId = "verify-account";

    private static SmtpCommandException Smtp(SmtpStatusCode status) =>
        new(SmtpErrorCode.MessageNotAccepted, status, ((int)status).ToString());

    public static IEnumerable<object[]> Failures =>
    [
        ["imap", "auth", new MailKit.Security.AuthenticationException("bad password"), "imap", "auth_failed"],
        ["imap", "auth", new MailKit.Security.AuthenticationException("The LOGIN command is disabled."), "imap", "tls_failed"],
        ["imap", "auth", new ImapProtocolException("BYE"), "imap", "auth_failed"],
        ["imap", "connect", new NotSupportedException("The IMAP server does not support the STARTTLS extension."), "imap", "tls_failed"],
        ["imap", "connect", new SslHandshakeException("handshake failed", new IOException("reset")), "imap", "tls_failed"],
        ["imap", "connect", new SocketException(), "imap", "unreachable"],
        ["imap", "connect", new TimeoutException(), "imap", "unreachable"],
        ["imap", "connect", new IOException("Failed to resolve host"), "imap", "unreachable"],
        ["imap", "connect", new PrivateAddressException("mail.example.com", IPAddress.Loopback), "imap", "unreachable"],
        ["imap", "connect", new OperationCanceledException(), "imap", "unreachable"],
        ["imap", "read", new ImapCommandException(ImapCommandResponse.No, "EXAMINE failed"), "imap", "mailbox_refused"],
        ["imap", "read", new FolderNotFoundException("Watched", "no such folder"), "imap", "mailbox_refused"],
        ["imap", "read", new ImapProtocolException("BYE"), "imap", "unreachable"],
        ["imap", "read", new CryptographicException(), "imap", "auth_failed"],
        // MailKit's SaslPrep (SCRAM, RFC 4013) refuses a control or prohibited character in the credentials.
        ["imap", "auth", new ArgumentException("Control characters are prohibited.", "s"), "imap", "auth_failed"],
        ["smtp", "auth", new ArgumentException("One or more characters in the string are prohibited.", "s"), "smtp", "smtp_auth_failed"],
        ["smtp", "auth", new MailKit.Security.AuthenticationException("x", Smtp(SmtpStatusCode.AuthenticationInvalidCredentials)), "smtp", "smtp_auth_failed"],
        ["smtp", "auth", new MailKit.Security.AuthenticationException("x", Smtp((SmtpStatusCode)454)), "smtp", "smtp_unreachable"],
        ["smtp", "auth", Smtp(SmtpStatusCode.AuthenticationInvalidCredentials), "smtp", "smtp_auth_failed"],
        ["smtp", "auth", new NotSupportedException("No compatible authentication mechanisms found."), "smtp", "smtp_auth_failed"],
        ["smtp", "auth", new SaslException("PLAIN", SaslErrorCode.MissingChallenge, "x"), "smtp", "smtp_auth_failed"],
        ["smtp", "connect", new NotSupportedException("The SMTP server does not support the STARTTLS extension."), "smtp", "tls_failed"],
        ["smtp", "connect", new SslHandshakeException("handshake failed", new IOException("reset")), "smtp", "tls_failed"],
        ["smtp", "connect", new SocketException(), "smtp", "smtp_unreachable"],
        ["smtp", "connect", new SmtpProtocolException("closed"), "smtp", "smtp_unreachable"],
    ];

    [TestMethod]
    [DynamicData(nameof(Failures))]
    public void Failure_IsNamedForWhatThePersonFixes(string step, string stage, Exception failure, string expectedStep, string expectedReason)
    {
        Assert.AreEqual(new VerifyFailure(expectedStep, expectedReason), VerifyReasons.From(step, stage, failure));
    }

    [TestMethod]
    public void ABug_IsNotAVerifyReason()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => VerifyReasons.From("imap", "read", new NullReferenceException()));
        Assert.ThrowsExactly<InvalidOperationException>(() => VerifyReasons.From("imap", "auth", new ArgumentNullException("password")));
        Assert.ThrowsExactly<InvalidOperationException>(() => VerifyReasons.From("imap", "connect", new ArgumentException("bad host")));
    }

    [TestMethod]
    public async Task Verify_APasswordScramCannotPrepare_IsAuthFailed_AndTheLogHoldsNoPassword()
    {
        await using var scram = ScriptedMailServer.ImapWithScram();
        var logs = new CapturingLogs();
        var config = Config(scram.Address, ("127.0.0.1", ClosedPort()));
        config["password"] = "pasted-with-a-newline\n";

        var outcome = await Verifier(logs).VerifyAsync(AccountId, config, true, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("imap", "auth_failed"), null), outcome);
        var line = logs.Lines.Single();
        Assert.Contains("ArgumentException", line);
        Assert.DoesNotContain("pasted-with-a-newline", line);
    }

    [TestMethod]
    [DataRow(null, SecureSocketOptions.Auto)]
    [DataRow("ssl", SecureSocketOptions.SslOnConnect)]
    [DataRow("starttls", SecureSocketOptions.StartTls)]
    [DataRow("none", SecureSocketOptions.None)]
    public void Security_IsTheMailKitOption(string? security, SecureSocketOptions expected)
    {
        Assert.AreEqual(expected, ImapConnections.Options(security));
    }

    [TestMethod]
    public void ImapAccountConfig_ToStringHidesThePassword()
    {
        var cfg = ImapProviderService.ImapAccountConfig.From(AccountId, Config(("imap.example.com", 993), ("smtp.example.com", 465)), Protector());

        var printed = cfg.ToString();

        Assert.DoesNotContain(ImapMailbox.Password, printed);
        Assert.Contains("imap.example.com", printed);
    }

    [TestMethod]
    public async Task Verify_RightPassword_Passes()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        await mailbox.DeliverAsync(ImapMailbox.Mail());

        var outcome = await Verifier().VerifyAsync(
            AccountId, Config(ImapServers.Dovecot, ImapServers.GreenMailSmtp, mailbox.Account.ProviderConfig["username"]), true, default);

        Assert.AreEqual(new VerifyOutcome(null, null), outcome);
    }

    [TestMethod]
    public async Task Verify_WrongPassword_IsAuthFailed_AndLogsTheServersLine()
    {
        var logs = new CapturingLogs();
        var config = Config(ImapServers.Dovecot, ImapServers.GreenMailSmtp);
        config["password"] = "wrong-" + ImapMailbox.Password;

        var outcome = await Verifier(logs).VerifyAsync(AccountId, config, true, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("imap", "auth_failed"), null), outcome);
        var line = logs.Lines.Single();
        Assert.Contains(AccountId, line);
        Assert.Contains("AuthenticationException", line);
        Assert.Contains("Authentication failed", line, "Dovecot's NO text");
        Assert.DoesNotContain(ImapMailbox.Password, line);
    }

    [TestMethod]
    public async Task Verify_StartTlsMissing_IsTlsFailed()
    {
        var config = Config(ImapServers.Dovecot, ImapServers.GreenMailSmtp);
        config["imapSecurity"] = "starttls";

        var outcome = await Verifier().VerifyAsync(AccountId, config, true, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("imap", "tls_failed"), null), outcome);
    }

    [TestMethod]
    public async Task Verify_ClosedPort_IsUnreachable()
    {
        var outcome = await Verifier().VerifyAsync(AccountId, Config(("127.0.0.1", ClosedPort()), ImapServers.GreenMailSmtp), true, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("imap", "unreachable"), null), outcome);
    }

    [TestMethod]
    public async Task Verify_PrivateAddressForAMember_IsRefused()
    {
        var logs = new CapturingLogs();

        var outcome = await Verifier(logs).VerifyAsync(AccountId, Config(ImapServers.Dovecot, ImapServers.GreenMailSmtp), false, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("imap", "unreachable"), null), outcome);
        var line = logs.Lines.Single();
        Assert.Contains("PrivateAddressException", line);
        Assert.Contains($"'{ImapServers.Dovecot.Host}' resolves to", line, "the log names the refused host and its address");
    }

    [TestMethod]
    public void ServerLine_OfAPrivateAddress_NamesTheHostAndTheAddress()
    {
        var line = VerifyReasons.ServerLine(new PrivateAddressException("mail.example.com", IPAddress.Parse("10.1.2.3")));

        Assert.Contains("mail.example.com", line);
        Assert.Contains("10.1.2.3", line);
    }

    [TestMethod]
    public async Task Verify_TheBudgetFiring_IsUnreachable_WhateverTheClientThrew()
    {
        var logs = new CapturingLogs();
        // What MailKit throws when its read is cut short depends on where the cut lands; this one names TLS.
        var guard = new HostGuard(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") }),
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
                throw new NotSupportedException("The IMAP server does not support the STARTTLS extension.");
            });
        var verifier = Verifier(logs, guard: guard);
        verifier.ImapBudget = TimeSpan.FromMilliseconds(200);

        var outcome = await verifier.VerifyAsync(AccountId, Config(("mail.example.com", 993), ("smtp.example.com", 465)), false, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("imap", "unreachable"), null), outcome);
        Assert.Contains("NotSupportedException", logs.Lines.Single());
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("65536")]
    [DataRow("-1")]
    public void ImapAccountConfig_PortOutOfRange_IsTheDefault(string port)
    {
        var config = Config(("imap.example.com", 993), ("smtp.example.com", 465));
        config["imapPort"] = port;

        Assert.AreEqual(ImapProviderService.DefaultImapPort, ImapProviderService.ImapAccountConfig.From(AccountId, config, Protector()).ImapPort);
    }

    [TestMethod]
    public async Task Verify_PrivateAddressForAnAdmin_Connects()
    {
        var outcome = await Verifier().VerifyAsync(AccountId, Config(ImapServers.Dovecot, ImapServers.GreenMailSmtp), true, default);

        Assert.AreEqual(new VerifyOutcome(null, null), outcome);
    }

    [TestMethod]
    public async Task Verify_SilentServer_IsUnreachableAtTheBudget()
    {
        await using var silent = ScriptedMailServer.Silent();
        var verifier = Verifier();
        verifier.ImapBudget = TimeSpan.FromMilliseconds(300);

        var outcome = await verifier.VerifyAsync(AccountId, Config(silent.Address, ImapServers.GreenMailSmtp), true, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("imap", "unreachable"), null), outcome);
    }

    [TestMethod]
    public async Task Verify_TheCallerGoingAway_IsNotAVerdict()
    {
        await using var silent = ScriptedMailServer.Silent();
        using var gone = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Verifier().VerifyAsync(AccountId, Config(silent.Address, ImapServers.GreenMailSmtp), true, gone.Token));
    }

    [TestMethod]
    public async Task Verify_SmtpDown_PassesWithAWarning()
    {
        var outcome = await Verifier().VerifyAsync(AccountId, Config(ImapServers.Dovecot, ("127.0.0.1", ClosedPort())), true, default);

        Assert.AreEqual(new VerifyOutcome(null, "smtp_unreachable"), outcome);
    }

    [TestMethod]
    public async Task Verify_SmtpWrongPassword_Blocks()
    {
        var logs = new CapturingLogs();

        // Dovecot takes any user with the fixture password; the GreenMail with auth holds this user with another one.
        var outcome = await Verifier(logs).VerifyAsync(
            AccountId, Config(ImapServers.Dovecot, ImapServers.GreenMailAuthSmtp, ImapServers.GreenMailAuthUser), true, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("smtp", "smtp_auth_failed"), null), outcome);
        Assert.Contains("535", logs.Lines.Single());
    }

    [TestMethod]
    public async Task Verify_SmtpWithoutAuth_IsSmtpAuthFailed()
    {
        await using var noAuth = ScriptedMailServer.SmtpWithoutAuth();

        var outcome = await Verifier().VerifyAsync(AccountId, Config(ImapServers.Dovecot, noAuth.Address), true, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("smtp", "smtp_auth_failed"), null), outcome);
    }

    [TestMethod]
    public async Task Verify_RunsTheWatchsFirstRead()
    {
        var config = Config(ImapServers.Dovecot, ImapServers.GreenMailSmtp);
        config["inboxFolder"] = "Watched";

        var outcome = await Verifier().VerifyAsync(AccountId, config, true, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("imap", "mailbox_refused"), null), outcome);
    }

    [TestMethod]
    public async Task Verify_StoredEncryptedPassword_LogsIn()
    {
        var protector = Protector();
        var config = Config(ImapServers.Dovecot, ImapServers.GreenMailSmtp);
        config["password"] = protector.Protect(ImapMailbox.Password);

        var outcome = await Verifier(protector: protector).VerifyAsync(AccountId, config, true, default);

        Assert.AreEqual(new VerifyOutcome(null, null), outcome);
    }

    [TestMethod]
    public async Task Verify_PasswordFromAnotherKeyRing_IsAuthFailed()
    {
        var config = Config(ImapServers.Dovecot, ImapServers.GreenMailSmtp);
        config["password"] = Protector().Protect(ImapMailbox.Password);

        var outcome = await Verifier(protector: Protector()).VerifyAsync(AccountId, config, true, default);

        Assert.AreEqual(new VerifyOutcome(new VerifyFailure("imap", "auth_failed"), null), outcome);
    }

    private static Dictionary<string, string> Config(
        (string Host, int Port) imap, (string Host, int Port) smtp, string? username = null) => new()
    {
        ["imapHost"] = imap.Host,
        ["imapPort"] = imap.Port.ToString(),
        ["imapSecurity"] = "none",
        ["smtpHost"] = smtp.Host,
        ["smtpPort"] = smtp.Port.ToString(),
        ["smtpSecurity"] = "none",
        ["username"] = username ?? $"{Guid.NewGuid():N}@example.com",
        ["password"] = ImapMailbox.Password
    };

    private static PasswordProtector Protector() => new(new EphemeralDataProtectionProvider());

    private static ImapAccountVerifier Verifier(
        CapturingLogs? logs = null, PasswordProtector? protector = null, HostGuard? guard = null) => new(
        guard ?? new HostGuard(),
        protector ?? Protector(),
        LoggerFactory.Create(builder => builder.AddProvider(logs ?? new CapturingLogs())).CreateLogger<ImapAccountVerifier>());

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
