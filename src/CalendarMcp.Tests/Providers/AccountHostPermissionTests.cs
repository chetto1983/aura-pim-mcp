using System.Net;
using System.Net.Sockets;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Security;
using CalendarMcp.Tests.Helpers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// Every connection for a stored IMAP account goes through <see cref="HostGuard"/> with the permission the account
/// was stored with, not only the check at save time. A member's host that resolved publicly, or not at all, when it
/// was saved can later point at the PIM's own network: the first send or the next watch tick would then hand the
/// password to whatever answers there. The guard here resolves every name to 10.0.0.5, as that DNS now does, and
/// sends a connection it lets through to a scripted server, so what reached a socket is what the server heard.
/// </summary>
[TestClass]
public class AccountHostPermissionTests
{
    private const string MemberHost = "localhost";
    private const string AccountId = "posta";
    private const string Password = "la-password";

    private sealed class PrivateDns(ScriptedMailServer server)
    {
        private int _connects;

        public int Connects => Volatile.Read(ref _connects);

        public HostGuard Guard => new(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }),
            async (endpoint, ct) =>
            {
                Interlocked.Increment(ref _connects);
                if (endpoint.Port != server.Address.Port)
                    throw new SocketException((int)SocketError.ConnectionRefused);

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Address.Port), ct);
                return socket;
            });
    }

    /// <param name="allowPrivateHosts">What the account was stored with; null is an account stored before the key.</param>
    private static AccountInfo Account(int imapPort, int smtpPort, string? allowPrivateHosts)
    {
        var config = new Dictionary<string, string>
        {
            ["imapHost"] = MemberHost,
            ["imapPort"] = imapPort.ToString(),
            ["imapSecurity"] = "none",
            ["smtpHost"] = MemberHost,
            ["smtpPort"] = smtpPort.ToString(),
            ["smtpSecurity"] = "none",
            ["username"] = "member@example.com",
            ["password"] = Password,
            ["sentFolder"] = "Sent"
        };
        if (allowPrivateHosts is not null)
            config[ImapProviderService.AllowPrivateHostsKey] = allowPrivateHosts;
        return TestData.CreateAccount(id: AccountId, provider: "imap", providerConfig: config);
    }

    private static ImapProviderService Provider(AccountInfo account, HostGuard guard) => new(
        new SingleAccountRegistry(account),
        new PasswordProtector(new EphemeralDataProtectionProvider()),
        guard,
        NullLogger<ImapProviderService>.Instance);

    private static Task<string> SendAsync(ImapProviderService provider) =>
        provider.SendEmailAsync(AccountId, "someone@example.org", "Prova", "Testo", bodyFormat: "text");

    // ── Sending ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task Send_ForAMemberMadeAccount_ToAHostNowPrivate_FailsWithoutConnecting()
    {
        await using var smtp = ScriptedMailServer.SmtpWithAuth();
        var dns = new PrivateDns(smtp);
        await using var provider = Provider(Account(993, smtp.Address.Port, "false"), dns.Guard);

        await Assert.ThrowsExactlyAsync<PrivateAddressException>(() => SendAsync(provider));

        Assert.AreEqual(0, dns.Connects, "the guard refused before opening a socket");
        Assert.AreEqual(0, smtp.Connections, "nothing reached the server");
        Assert.IsEmpty(smtp.Heard, "so neither did the password");
    }

    [TestMethod]
    [DataRow("true", "an account the administrator stored")]
    [DataRow(null, "an account stored before the permission was recorded: the operator's on-premises server")]
    public async Task Send_ForAnAccountAllowedPrivateHosts_ConnectsThroughTheGuard(string? allowPrivateHosts, string why)
    {
        await using var smtp = ScriptedMailServer.SmtpWithAuth();
        var dns = new PrivateDns(smtp);
        await using var provider = Provider(Account(993, smtp.Address.Port, allowPrivateHosts), dns.Guard);

        await SendAsync(provider);

        Assert.AreEqual(1, smtp.Connections, why);
        Assert.IsTrue(smtp.Heard.Any(line => line.StartsWith("AUTH PLAIN", StringComparison.Ordinal)), "it logged in");
        Assert.IsTrue(smtp.Heard.Contains("."), "and the mail went out");
        // The send connects once, and the copy to Sent tries IMAP on 993, which the guard's seam refuses.
        Assert.AreEqual(2, dns.Connects, "both connections went through the guard");
    }

    // ── Reading: the watch and the pool ─────────────────────────────

    [TestMethod]
    public async Task Read_ForAMemberMadeAccount_FromAHostNowPrivate_FailsWithoutConnecting()
    {
        await using var imap = ScriptedMailServer.ImapWithEmptyInbox();
        var dns = new PrivateDns(imap);
        await using var provider = Provider(Account(imap.Address.Port, 587, "false"), dns.Guard);

        await Assert.ThrowsExactlyAsync<PrivateAddressException>(() => provider.StartAsync(AccountId, default));

        Assert.AreEqual(0, dns.Connects);
        Assert.AreEqual(0, imap.Connections);
        Assert.IsEmpty(imap.Heard);
    }

    [TestMethod]
    public async Task Read_ForAnAdminMadeAccount_ConnectsThroughTheGuard()
    {
        await using var imap = ScriptedMailServer.ImapWithEmptyInbox();
        var dns = new PrivateDns(imap);
        await using var provider = Provider(Account(imap.Address.Port, 587, "true"), dns.Guard);

        await provider.StartAsync(AccountId, default);

        Assert.AreEqual(1, dns.Connects);
        Assert.AreEqual(1, imap.Connections);
    }

    [TestMethod]
    public async Task Read_AfterAMemberTookTheAccountOver_DoesNotReuseTheAdminsSession()
    {
        await using var imap = ScriptedMailServer.ImapWithEmptyInbox();
        var dns = new PrivateDns(imap);
        var account = Account(imap.Address.Port, 587, "true");
        await using var provider = Provider(account, dns.Guard);
        await provider.StartAsync(AccountId, default);

        account.ProviderConfig[ImapProviderService.AllowPrivateHostsKey] = "false";

        await Assert.ThrowsExactlyAsync<PrivateAddressException>(() => provider.StartAsync(AccountId, default));
        Assert.AreEqual(1, imap.Connections, "the pooled session was dropped, and no new one was opened");
    }

    // ── How the key reads ───────────────────────────────────────────

    [TestMethod]
    [DataRow(null, true, "absent: stored before the key, by the operator")]
    [DataRow("true", true, "")]
    [DataRow("TRUE", true, "")]
    [DataRow("false", false, "")]
    [DataRow("", false, "there but blank is not absent")]
    [DataRow("yes", false, "anything but true fails closed")]
    public void AllowPrivateHosts_Reads(string? stored, bool expected, string why)
    {
        var config = Account(993, 587, stored).ProviderConfig;

        var read = ImapProviderService.ImapAccountConfig.From(
            AccountId, config, new PasswordProtector(new EphemeralDataProtectionProvider()));

        Assert.AreEqual(expected, read.AllowPrivateHosts, why);
    }
}
