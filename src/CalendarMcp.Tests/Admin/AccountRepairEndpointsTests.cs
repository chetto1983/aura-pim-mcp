using System.Text.Json;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Security;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Tests.Helpers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarMcp.Tests.Admin;

/// <summary>
/// Connecting and repairing an IMAP account through the admin API: nothing is stored that the mailbox did not
/// accept, a password is stored encrypted, a partial change keeps everything it does not name, and no request
/// sends the stored password to a server it did not name before. <c>CALENDAR_MCP_CONFIG</c> is process-wide.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class AccountRepairEndpointsTests
{
    private static readonly string NewId = TenantIdentity.AccountId(TestData.TenantA, "nuova");

    private static Dictionary<string, string> ImapConfig(string password = AdminPipeline.Password) => new()
    {
        ["imapHost"] = "imap.example.org",
        ["imapPort"] = "993",
        ["smtpHost"] = "smtp.example.org",
        ["smtpPort"] = "465",
        ["username"] = "nuova@example.org",
        ["password"] = password
    };

    private static object Create(Dictionary<string, string> providerConfig, string id = "nuova", string provider = "imap") => new
    {
        id,
        displayName = "Nuova",
        provider,
        providerConfig
    };

    private static object Patch(Dictionary<string, string> providerConfig) => new { providerConfig };

    private static string Verify(string accountId) => $"/admin/accounts/{accountId}/verify";

    private static string ProviderConfig(string accountId) => $"/admin/accounts/{accountId}/provider-config";

    private static bool Exists(AdminPipeline pipeline, string accountId) =>
        File.ReadAllText(pipeline.ConfigPath).Contains($"\"{accountId}\"");

    private static void AssertVerifyFailed(JsonElement body, string step, string reason)
    {
        Assert.AreEqual(
            JsonSerializer.Serialize(new { error = "verify_failed", step, reason }),
            body.GetRawText());
    }

    // ── Create ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task Create_Imap_WrongPassword_Is422_AndStoresNothing()
    {
        var verifier = new FakeVerifier { Answer = FakeVerifier.PasswordIs(AdminPipeline.Password) };
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(ImapConfig("wrong")));

        Assert.AreEqual(422, status);
        AssertVerifyFailed(body, "imap", "auth_failed");
        Assert.IsFalse(Exists(pipeline, NewId));
    }

    [TestMethod]
    public async Task Create_Imap_RightPassword_StoresItEncrypted_WithItsSecurity()
    {
        var verifier = new FakeVerifier { Answer = FakeVerifier.PasswordIs(AdminPipeline.Password) };
        await using var pipeline = new AdminPipeline(verifier);
        var config = ImapConfig();
        config["smtpPort"] = "587";

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(config));

        Assert.AreEqual(201, status);
        Assert.IsFalse(body.TryGetProperty("smtpWarning", out _));
        var stored = pipeline.StoredConfig(NewId);
        var password = stored["password"]!.GetValue<string>();
        Assert.IsTrue(PasswordProtector.IsProtected(password));
        Assert.AreEqual(AdminPipeline.Password, pipeline.Protector.Unprotect(password));
        Assert.AreEqual("ssl", stored["imapSecurity"]!.GetValue<string>());
        Assert.AreEqual("starttls", stored["smtpSecurity"]!.GetValue<string>());
        var (_, listed) = await pipeline.GetAsync("/admin/accounts");
        Assert.Contains(NewId, listed.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("id").GetString()),
            "the registry holds the new account when the answer leaves");
        var call = verifier.Calls.Single();
        Assert.AreEqual(NewId, call.AccountId);
        Assert.AreEqual("ssl", call.Read!.ImapSecurity, "the check uses the security that is stored");
        Assert.AreEqual("starttls", call.Read.SmtpSecurity);
    }

    [TestMethod]
    [DataRow("none", "tls_required")]
    [DataRow("NONE", "tls_required")]
    [DataRow("auto", "invalid_security")]
    public async Task Create_SecurityNone_Is400(string security, string error)
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var config = ImapConfig();
        config["imapSecurity"] = security;

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(config));

        Assert.AreEqual(400, status);
        Assert.AreEqual(error, body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls);
        Assert.IsFalse(Exists(pipeline, NewId));
    }

    [TestMethod]
    [DataRow("imapPort", "0")]
    [DataRow("smtpPort", "65536")]
    [DataRow("imapPort", "imap")]
    public async Task Create_PortOutOfRange_Is400_WithoutVerifying(string key, string port)
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var config = ImapConfig();
        config[key] = port;

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(config));

        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_port", body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    [DataRow("imapHost", "imap example.org")]
    [DataRow("smtpHost", "smtp.example.org/")]
    public async Task Create_HostNoResolverTakes_Is400_WithoutVerifying(string key, string host)
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var config = ImapConfig();
        config[key] = host;

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(config));

        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_host", body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    public async Task Create_HostLongerThanDnsCarries_Is400()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var config = ImapConfig();
        config["imapHost"] = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)) + ".example.org";

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(config));

        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_host", body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Create_WithoutAPassword_Is400_WithoutVerifying()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, _) = await pipeline.PostAsync("/admin/accounts", Create(ImapConfig(password: "")));

        Assert.AreEqual(400, status);
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    public async Task Create_SmtpUnreachable_Is201WithAWarning()
    {
        var verifier = new FakeVerifier { Answer = _ => new VerifyOutcome(null, VerifyReasons.SmtpUnreachable) };
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(ImapConfig()));

        Assert.AreEqual(201, status);
        Assert.AreEqual("smtp_unreachable", body.GetProperty("smtpWarning").GetString());
        Assert.IsTrue(Exists(pipeline, NewId));
    }

    [TestMethod]
    public async Task Create_Ics_IsNotVerified()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, _) = await pipeline.PostAsync("/admin/accounts",
            Create(new Dictionary<string, string> { ["IcsUrl"] = "https://calendar.example.com/feed.ics" }, provider: "ics"));

        Assert.AreEqual(201, status);
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    public async Task Create_DuplicateId_Is409_WithoutVerifying()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var before = File.ReadAllText(pipeline.ConfigPath);

        var (status, _) = await pipeline.PostAsync("/admin/accounts", Create(ImapConfig(), id: "mail"));

        Assert.AreEqual(409, status);
        Assert.IsEmpty(verifier.Calls);
        Assert.AreEqual(before, File.ReadAllText(pipeline.ConfigPath));
        Assert.AreEqual(200, (await pipeline.PostAsync(Verify(AdminPipeline.MailId))).Status,
            "a refused duplicate does not spend the existing account's check");
    }

    [TestMethod]
    public async Task Create_PasswordStartingWithENC_IsStillEncrypted()
    {
        const string typed = "ENC:what-the-person-typed";
        var verifier = new FakeVerifier { Answer = FakeVerifier.PasswordIs(typed) };
        await using var pipeline = new AdminPipeline(verifier);

        var (status, _) = await pipeline.PostAsync("/admin/accounts", Create(ImapConfig(typed)));

        Assert.AreEqual(201, status, "the check reads the password as typed");
        var stored = pipeline.StoredConfig(NewId)["password"]!.GetValue<string>();
        Assert.AreNotEqual(typed, stored);
        Assert.AreEqual(typed, pipeline.Protector.Unprotect(stored));
    }

    [TestMethod]
    public async Task Create_PassesThePrivateHostsHeaderToTheVerifier()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        await pipeline.PostAsync("/admin/accounts", Create(ImapConfig(), id: "admin"), allowPrivate: true);
        await pipeline.PostAsync("/admin/accounts", Create(ImapConfig(), id: "member"));

        CollectionAssert.AreEqual(new[] { true, false }, verifier.Calls.Select(call => call.AllowPrivate).ToArray());
    }

    [TestMethod]
    public async Task Create_WithTheRealVerifier_RefusesAPrivateHostForAMember()
    {
        await using var pipeline = new AdminPipeline(verifier: null);
        var config = ImapConfig();
        config["imapHost"] = "127.0.0.1";

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(config));

        Assert.IsInstanceOfType<ImapAccountVerifier>(pipeline.Services.GetRequiredService<IImapAccountVerifier>());
        Assert.AreEqual(422, status);
        AssertVerifyFailed(body, "imap", "unreachable");
        Assert.IsFalse(Exists(pipeline, NewId));
    }

    // What the real verifier answers for a private SMTP host is in ImapAccountVerifierTests: through the endpoints a
    // member cannot name a plaintext IMAP server, so no real one is reachable here. This is what the endpoints do
    // with that answer, and the header decides it as the real guard does.
    private static Func<ImapProviderService.ImapAccountConfig, VerifyOutcome> PrivateSmtpRefusedUnlessAdmin(FakeVerifier verifier) =>
        _ => verifier.Calls[^1].AllowPrivate
            ? new VerifyOutcome(null, VerifyReasons.SmtpUnreachable)
            : new VerifyOutcome(new VerifyFailure(VerifyReasons.Smtp, VerifyReasons.SmtpUnreachable), null);

    [TestMethod]
    public async Task Create_PrivateSmtpHostForAMember_Is422_AndStoresNothing()
    {
        var verifier = new FakeVerifier();
        verifier.Answer = PrivateSmtpRefusedUnlessAdmin(verifier);
        await using var pipeline = new AdminPipeline(verifier);
        var config = ImapConfig();
        config["smtpHost"] = "10.0.0.5";

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(config));

        Assert.AreEqual(422, status);
        AssertVerifyFailed(body, "smtp", "smtp_unreachable");
        Assert.IsFalse(Exists(pipeline, NewId));
    }

    [TestMethod]
    public async Task Create_PrivateSmtpHostForAnAdmin_Is201()
    {
        var verifier = new FakeVerifier();
        verifier.Answer = PrivateSmtpRefusedUnlessAdmin(verifier);
        await using var pipeline = new AdminPipeline(verifier);
        var config = ImapConfig();
        config["smtpHost"] = "10.0.0.5";

        var (status, _) = await pipeline.PostAsync("/admin/accounts", Create(config), allowPrivate: true);

        Assert.AreEqual(201, status);
        Assert.IsTrue(Exists(pipeline, NewId));
    }

    // ── Verify ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task Verify_PassingAccount_IsOk()
    {
        var verifier = new FakeVerifier { Answer = FakeVerifier.PasswordIs(AdminPipeline.Password) };
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PostAsync(Verify(AdminPipeline.MailId));

        Assert.AreEqual(200, status);
        Assert.AreEqual("""{"ok":true}""", body.GetRawText());
    }

    [TestMethod]
    public async Task Verify_SmtpWarning_IsInTheAnswer()
    {
        var verifier = new FakeVerifier { Answer = _ => new VerifyOutcome(null, VerifyReasons.TlsFailed) };
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PostAsync(Verify(AdminPipeline.MailId));

        Assert.AreEqual(200, status);
        Assert.AreEqual("tls_failed", body.GetProperty("smtpWarning").GetString());
    }

    [TestMethod]
    public async Task Verify_FailingAccount_Is422WithExactlyErrorStepReason()
    {
        var verifier = new FakeVerifier { Answer = _ => new VerifyOutcome(new VerifyFailure("imap", "mailbox_refused"), null) };
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PostAsync(Verify(AdminPipeline.MailId));

        Assert.AreEqual(422, status);
        AssertVerifyFailed(body, "imap", "mailbox_refused");
    }

    [TestMethod]
    public async Task Verify_OnAForeignAccount_IsNotFound()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, _) = await pipeline.PostAsync(Verify(AdminPipeline.ForeignMailId));

        Assert.AreEqual(404, status);
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    public async Task Verify_OnAGoogleAccount_IsNotVerifiable()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PostAsync(Verify(AdminPipeline.GmailId));

        Assert.AreEqual(400, status);
        Assert.AreEqual("not_verifiable", body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Verify_AStoredConfigThatCannotConnect_IsNotVerifiable()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        pipeline.SetStored(AdminPipeline.MailId, "imapSecurity", "sometimes");

        var (status, body) = await pipeline.PostAsync(Verify(AdminPipeline.MailId));

        Assert.AreEqual(400, status);
        Assert.AreEqual("not_verifiable", body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    public async Task Verify_UnreadableStoredPassword_IsAuthFailed()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var otherRing = new PasswordProtector(new EphemeralDataProtectionProvider());
        pipeline.SetStored(AdminPipeline.MailId, "password", otherRing.Protect(AdminPipeline.Password));

        var (status, body) = await pipeline.PostAsync(Verify(AdminPipeline.MailId));

        Assert.AreEqual(422, status);
        AssertVerifyFailed(body, "imap", "auth_failed");
        Assert.IsNull(verifier.Calls.Single().Read);
    }

    [TestMethod]
    public async Task Verify_TwiceWithin30s_Is429_WithoutConnecting()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        Assert.AreEqual(200, (await pipeline.PostAsync(Verify(AdminPipeline.MailId))).Status);
        pipeline.Clock.Now += TimeSpan.FromSeconds(29);
        var (status, body) = await pipeline.PostAsync(Verify(AdminPipeline.MailId));
        pipeline.Clock.Now += TimeSpan.FromSeconds(1);
        var later = await pipeline.PostAsync(Verify(AdminPipeline.MailId));

        Assert.AreEqual(429, status);
        Assert.AreEqual("too_many_attempts", body.GetProperty("error").GetString());
        Assert.AreEqual(200, later.Status);
        Assert.HasCount(2, verifier.Calls);
    }
}
