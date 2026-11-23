using System.Text.Json.Nodes;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Security;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarMcp.Tests.Admin;

public sealed partial class AccountRepairEndpointsTests
{
    private static JsonObject WithoutPassword(JsonObject account)
    {
        var copy = (JsonObject)account.DeepClone();
        ((JsonObject)copy["ProviderConfig"]!).Remove("password");
        return copy;
    }

    [TestMethod]
    public async Task Patch_ChangesOnlyThePassword()
    {
        var verifier = new FakeVerifier { Answer = FakeVerifier.PasswordIs("nuova") };
        await using var pipeline = new AdminPipeline(verifier);
        var before = pipeline.StoredAccount(AdminPipeline.MailId);

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "nuova" }));

        Assert.AreEqual(200, status);
        var after = pipeline.StoredAccount(AdminPipeline.MailId);
        Assert.AreEqual(WithoutPassword(before).ToJsonString(), WithoutPassword(after).ToJsonString());
        Assert.AreEqual("nuova", pipeline.Protector.Unprotect(after["ProviderConfig"]!["password"]!.GetValue<string>()));
        Assert.AreEqual(AdminPipeline.MailId, body.GetProperty("id").GetString());
        Assert.AreEqual("Posta", body.GetProperty("displayName").GetString());
        Assert.AreEqual(3, body.GetProperty("priority").GetInt32());
        Assert.IsFalse(body.GetProperty("permissions").GetProperty("granted").GetProperty("emailSend").GetBoolean());
        Assert.IsFalse(body.TryGetProperty("smtpWarning", out _));
    }

    [TestMethod]
    public async Task Patch_KeepsTheStoredCiphertextWhenNoPasswordIsSent()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var stored = pipeline.StoredConfig(AdminPipeline.MailId)["password"]!.GetValue<string>();

        var (status, _) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["inboxFolder"] = "Archivio" }));

        Assert.AreEqual(200, status);
        var after = pipeline.StoredConfig(AdminPipeline.MailId);
        Assert.AreEqual(stored, after["password"]!.GetValue<string>());
        Assert.AreEqual("Archivio", after["inboxFolder"]!.GetValue<string>());
        var call = verifier.Calls.Single();
        Assert.AreEqual(stored, call.Config["password"], "the kept ciphertext is what the check is handed");
        Assert.AreEqual(AdminPipeline.Password, call.Read!.Password, "and From reads it as plaintext");
        Assert.AreEqual("Archivio", call.Read.InboxFolder);
        using (pipeline.Services.GetRequiredService<ITenantContext>().Bind(TestData.TenantA))
        {
            var registered = await pipeline.Services.GetRequiredService<IAccountRegistry>().GetAccountAsync(AdminPipeline.MailId);
            Assert.AreEqual("Archivio", registered!.ProviderConfig["inboxFolder"], "the watch reads the change as soon as the answer leaves");
        }
    }

    [TestMethod]
    public async Task Patch_EmptyValues_AreUnchanged()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var before = pipeline.StoredAccount(AdminPipeline.MailId).ToJsonString();

        var (status, _) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "", ["imapHost"] = "" }));

        Assert.AreEqual(200, status);
        Assert.AreEqual(before, pipeline.StoredAccount(AdminPipeline.MailId).ToJsonString());
    }

    [TestMethod]
    [DataRow("imapHost", "imap.example.net")]
    [DataRow("imapPort", "143")]
    [DataRow("smtpHost", "smtp.example.net")]
    [DataRow("smtpPort", "587")]
    public async Task Patch_NewHostWithoutPassword_Is400_AndNothingConnects(string key, string value)
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var before = File.ReadAllText(pipeline.ConfigPath);

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { [key] = value }));

        Assert.AreEqual(400, status);
        Assert.AreEqual("password_required", body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls);
        Assert.AreEqual(before, File.ReadAllText(pipeline.ConfigPath));
    }

    [TestMethod]
    public async Task Patch_NewHostWithAPassword_IsVerifiedThere()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, _) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId),
            Patch(new() { ["imapHost"] = "imap.example.net", ["imapPort"] = "143", ["imapSecurity"] = "starttls", ["password"] = "nuova" }));

        Assert.AreEqual(200, status);
        var read = verifier.Calls.Single().Read!;
        Assert.AreEqual(("imap.example.net", 143, "starttls", "nuova"), (read.ImapHost, read.ImapPort, read.ImapSecurity, read.Password));
    }

    [TestMethod]
    public async Task Patch_NewUsername_Is400()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId),
            Patch(new() { ["username"] = "someone-else@example.com", ["password"] = "nuova" }));

        Assert.AreEqual(400, status);
        Assert.AreEqual("username_immutable", body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    public async Task Patch_DuplicateKeyCase_Is400()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId),
            AdminPipeline.Json("""{ "providerConfig": { "password": "one", "Password": "two" } }"""));

        Assert.AreEqual(400, status);
        Assert.AreEqual("duplicate_key", body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    public async Task Patch_SecurityNone_Is400()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["smtpSecurity"] = "none" }));

        Assert.AreEqual(400, status);
        Assert.AreEqual("tls_required", body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    public async Task Patch_AnAccountStoredWithoutSecurity_GetsItFromThePort()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        pipeline.SetStored(AdminPipeline.MailId, "imapSecurity", null);
        pipeline.SetStored(AdminPipeline.MailId, "smtpSecurity", null);
        pipeline.SetStored(AdminPipeline.MailId, "smtpPort", "587");

        var (status, _) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "nuova" }));

        Assert.AreEqual(200, status);
        var after = pipeline.StoredConfig(AdminPipeline.MailId);
        Assert.AreEqual("ssl", after["imapSecurity"]!.GetValue<string>());
        Assert.AreEqual("starttls", after["smtpSecurity"]!.GetValue<string>());
        Assert.AreEqual("starttls", verifier.Calls.Single().Read!.SmtpSecurity);
    }

    [TestMethod]
    public async Task Patch_OnAGoogleAccount_Is400()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.GmailId), Patch(new() { ["ClientSecret"] = "new" }));

        Assert.AreEqual(400, status);
        Assert.AreEqual("not_patchable", body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Patch_FailingVerify_Is422_AndTheFileIsUnchanged()
    {
        var verifier = new FakeVerifier { Answer = FakeVerifier.PasswordIs(AdminPipeline.Password) };
        await using var pipeline = new AdminPipeline(verifier);
        var before = File.ReadAllText(pipeline.ConfigPath);

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "sbagliata" }));

        Assert.AreEqual(422, status);
        AssertVerifyFailed(body, "imap", "auth_failed");
        Assert.AreEqual(before, File.ReadAllText(pipeline.ConfigPath));
    }

    [TestMethod]
    public async Task Patch_OnAForeignAccount_IsNotFound()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, _) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.ForeignMailId), Patch(new() { ["password"] = "nuova" }));

        Assert.AreEqual(404, status);
        Assert.IsEmpty(verifier.Calls);
    }

    [TestMethod]
    public async Task Patch_StoredConfigChangedMeanwhile_Is409()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        verifier.During = () => pipeline.SetStored(AdminPipeline.MailId, "inboxFolder", "Altro");
        var storedPassword = pipeline.StoredConfig(AdminPipeline.MailId)["password"]!.GetValue<string>();

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "nuova" }));

        Assert.AreEqual(409, status);
        Assert.AreEqual("changed", body.GetProperty("error").GetString());
        var after = pipeline.StoredConfig(AdminPipeline.MailId);
        Assert.AreEqual("Altro", after["inboxFolder"]!.GetValue<string>());
        Assert.AreEqual(storedPassword, after["password"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Patch_PasswordWithATrailingSpace_IsKeptAsTyped()
    {
        var verifier = new FakeVerifier { Answer = FakeVerifier.PasswordIs("nuova ") };
        await using var pipeline = new AdminPipeline(verifier);

        var (status, _) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "nuova " }));

        Assert.AreEqual(200, status);
        Assert.AreEqual("nuova ", pipeline.Protector.Unprotect(pipeline.StoredConfig(AdminPipeline.MailId)["password"]!.GetValue<string>()));
    }

    [TestMethod]
    public async Task Patch_SmtpWarning_IsInTheAnswer_AndTheChangeIsStored()
    {
        var verifier = new FakeVerifier { Answer = _ => new VerifyOutcome(null, VerifyReasons.SmtpUnreachable) };
        await using var pipeline = new AdminPipeline(verifier);

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "nuova" }));

        Assert.AreEqual(200, status);
        Assert.AreEqual("smtp_unreachable", body.GetProperty("smtpWarning").GetString());
        Assert.AreEqual("nuova", pipeline.Protector.Unprotect(pipeline.StoredConfig(AdminPipeline.MailId)["password"]!.GetValue<string>()));
    }

    [TestMethod]
    public async Task Patch_TwiceWithin30s_Is429()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "nuova" }));
        var (status, _) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "ancora" }));

        Assert.AreEqual(429, status);
        Assert.HasCount(1, verifier.Calls);
    }

    [TestMethod]
    public async Task Put_StoresThePasswordEncrypted()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var config = ImapConfig("nuova");

        var (status, _) = await pipeline.PutAsync($"/admin/accounts/{AdminPipeline.MailId}",
            new { displayName = "Posta", providerConfig = config });

        Assert.AreEqual(200, status);
        var stored = pipeline.StoredConfig(AdminPipeline.MailId)["password"]!.GetValue<string>();
        Assert.IsTrue(PasswordProtector.IsProtected(stored));
        Assert.AreEqual("nuova", pipeline.Protector.Unprotect(stored));
    }

    [TestMethod]
    public async Task Patch_PrivateSmtpHostForAMember_Is422_AndStoresNothing()
    {
        var verifier = new FakeVerifier();
        verifier.Answer = PrivateSmtpRefusedUnlessAdmin(verifier);
        await using var pipeline = new AdminPipeline(verifier);
        var before = File.ReadAllText(pipeline.ConfigPath);

        var (status, body) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId),
            Patch(new() { ["smtpHost"] = "10.0.0.5", ["password"] = "nuova" }));

        Assert.AreEqual(422, status);
        AssertVerifyFailed(body, "smtp", "smtp_unreachable");
        Assert.AreEqual(before, File.ReadAllText(pipeline.ConfigPath));
    }

    [TestMethod]
    public async Task Patch_PrivateSmtpHostForAnAdmin_Is200()
    {
        var verifier = new FakeVerifier();
        verifier.Answer = PrivateSmtpRefusedUnlessAdmin(verifier);
        await using var pipeline = new AdminPipeline(verifier);

        var (status, _) = await pipeline.PatchAsync(ProviderConfig(AdminPipeline.MailId),
            Patch(new() { ["smtpHost"] = "10.0.0.5", ["password"] = "nuova" }), allowPrivate: true);

        Assert.AreEqual(200, status);
        Assert.AreEqual("10.0.0.5", pipeline.StoredConfig(AdminPipeline.MailId)["smtpHost"]!.GetValue<string>());
    }
}
