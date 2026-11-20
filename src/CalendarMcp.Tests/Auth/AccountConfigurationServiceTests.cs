using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarMcp.Auth;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Security;
using CalendarMcp.HttpServer.Admin;
using Microsoft.AspNetCore.DataProtection;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Tests.Helpers;
using CalendarMcp.Tests.Changes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Auth;

[TestClass]
[DoNotParallelize]
public sealed class AccountConfigurationServiceTests
{
    private string _directory = null!;
    private string? _previousConfig;
    private TenantContext _tenantContext = null!;
    private IDisposable _tenantBinding = null!;
    private AccountConfigurationService _service = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "calendar-mcp-tenant-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _previousConfig = Environment.GetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable);
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _directory);
        File.WriteAllText(ConfigurationPaths.GetConfigFilePath(), InitialConfig());

        _tenantContext = new TenantContext();
        _tenantBinding = _tenantContext.Bind(TestData.TenantA);
        _service = new AccountConfigurationService(
            NullLogger<AccountConfigurationService>.Instance, _tenantContext);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _tenantBinding.Dispose();
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _previousConfig);
        Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task ReadsAndExplicitLookup_HideForeignAccount()
    {
        var listed = await _service.GetAllAccountsFromConfigAsync();

        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual("own", listed[0].Id);
        Assert.IsNull(await _service.GetAccountFromConfigAsync("foreign"));
        Assert.IsFalse(await _service.AccountExistsAsync("foreign"));
    }

    [TestMethod]
    public async Task ForeignMutation_IsReportedAsNotFoundAndLeavesFileIntact()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.RemoveAccountAsync("foreign"));

        var root = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath()))!;
        Assert.AreEqual(2, root["CalendarMcp"]!["Accounts"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task Add_PersistsOwnerAndGloballyUniqueAccountId()
    {
        var id = OwnId("new-account");
        var account = TestData.CreateAccount(id: id);

        await _service.AddAccountAsync(account);

        var added = await _service.GetAccountFromConfigAsync(id);
        Assert.IsNotNull(added);
        Assert.AreEqual(TestData.TenantA, added.TenantId);
    }

    [TestMethod]
    public async Task MissingTenantInAnyConfiguredAccount_FailsClosed()
    {
        await File.WriteAllTextAsync(ConfigurationPaths.GetConfigFilePath(), """
            { "CalendarMcp": { "Accounts": [
              { "Id": "unowned", "DisplayName": "Unowned", "Provider": "ics" }
            ] } }
            """);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => _ = await _service.GetAllAccountsFromConfigAsync());
    }

    [TestMethod]
    public async Task AddAccount_PersistsPermissions_AndReadsThemBack()
    {
        var id = OwnId("acc-1");
        var permissions = AccountPermissions.All
            .With(AccountPermission.EmailSend, false)
            .With(AccountPermission.CalendarWrite, false);

        await _service.AddAccountAsync(TestData.CreateAccount(id: id, permissions: permissions));

        var stored = await _service.GetAccountFromConfigAsync(id);

        Assert.IsNotNull(stored);
        Assert.IsTrue(stored.Permissions.EmailRead);
        Assert.IsFalse(stored.Permissions.EmailSend);
        Assert.IsTrue(stored.Permissions.CalendarRead);
        Assert.IsFalse(stored.Permissions.CalendarWrite);
        Assert.IsTrue(stored.Permissions.ContactsRead);
        Assert.IsTrue(stored.Permissions.ContactsWrite);
    }

    [TestMethod]
    public async Task AddAccount_WritesPermissionsAsCamelCaseJson()
    {
        var id = OwnId("acc-1");
        await _service.AddAccountAsync(TestData.CreateAccount(
            id: id,
            permissions: AccountPermissions.None.With(AccountPermission.EmailRead, true)));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath()));
        var written = doc.RootElement
            .GetProperty("CalendarMcp").GetProperty("Accounts")
            .EnumerateArray()
            .Single(a => a.GetProperty("Id").GetString() == id)
            .GetProperty("Permissions");

        Assert.IsTrue(written.GetProperty("emailRead").GetBoolean());
        Assert.IsFalse(written.GetProperty("emailSend").GetBoolean());
        Assert.IsFalse(written.GetProperty("contactsWrite").GetBoolean());
    }

    [TestMethod]
    public async Task GetAccount_ConfigWithNoPermissionsBlock_GrantsEverything()
    {
        // A config written before this feature existed.
        WriteOwnAccount("legacy", "microsoft365", permissionsJson: null);

        var stored = await _service.GetAccountFromConfigAsync("legacy");

        Assert.IsNotNull(stored);
        foreach (var permission in AccountPermissions.AllPermissions)
            Assert.IsTrue(stored.Permissions.IsGranted(permission), permission.ToString());
    }

    [TestMethod]
    public async Task GetAccount_PartialPermissionsBlock_DefaultsOmittedFlagsToGranted()
    {
        WriteOwnAccount("partial", "google", """{ "emailSend": false }""");

        var stored = await _service.GetAccountFromConfigAsync("partial");

        Assert.IsNotNull(stored);
        Assert.IsFalse(stored.Permissions.EmailSend);
        Assert.IsTrue(stored.Permissions.EmailRead);
        Assert.IsTrue(stored.Permissions.CalendarWrite);
    }

    [TestMethod]
    public async Task GetAccount_PascalCasePermissionsBlock_IsHonoured()
    {
        WriteOwnAccount("pascal", "google", """{ "EmailRead": false, "CalendarRead": false }""");

        var stored = await _service.GetAccountFromConfigAsync("pascal");

        Assert.IsNotNull(stored);
        Assert.IsFalse(stored.Permissions.EmailRead);
        Assert.IsFalse(stored.Permissions.CalendarRead);
        Assert.IsTrue(stored.Permissions.ContactsRead);
    }

    [TestMethod]
    public async Task UpdateAccount_ReplacesPermissions()
    {
        var id = OwnId("acc-1");
        await _service.AddAccountAsync(TestData.CreateAccount(id: id));

        var existing = await _service.GetAccountFromConfigAsync(id);
        Assert.IsNotNull(existing);

        await _service.UpdateAccountAsync(new AccountInfo
        {
            Id = existing.Id,
            TenantId = existing.TenantId,
            DisplayName = existing.DisplayName,
            Provider = existing.Provider,
            Domains = existing.Domains,
            Enabled = existing.Enabled,
            Priority = existing.Priority,
            Permissions = AccountPermissions.None.With(AccountPermission.CalendarRead, true),
            ProviderConfig = existing.ProviderConfig
        });

        var updated = await _service.GetAccountFromConfigAsync(id);

        Assert.IsNotNull(updated);
        Assert.IsTrue(updated.Permissions.CalendarRead);
        Assert.IsFalse(updated.Permissions.EmailRead);
        Assert.IsFalse(updated.Permissions.ContactsWrite);
    }

    [TestMethod]
    public async Task AddAccount_MultipleAccountsSameProvider_KeepIndependentPermissions()
    {
        var workId = OwnId("gmail-work");
        var personalId = OwnId("gmail-personal");

        await _service.AddAccountAsync(TestData.CreateAccount(
            id: workId, provider: "google",
            permissions: AccountPermissions.None.With(AccountPermission.EmailRead, true)));
        await _service.AddAccountAsync(TestData.CreateAccount(id: personalId, provider: "google"));

        var work = await _service.GetAccountFromConfigAsync(workId);
        var personal = await _service.GetAccountFromConfigAsync(personalId);

        Assert.IsNotNull(work);
        Assert.IsNotNull(personal);
        Assert.IsFalse(work.Permissions.EmailSend);
        Assert.IsTrue(personal.Permissions.EmailSend);
    }

    [TestMethod]
    public async Task ProtectStored_RewritesOnlyPlainPasswords()
    {
        var protector = Protector();
        var encrypted = protector.Protect("already");
        await File.WriteAllTextAsync(ConfigurationPaths.GetConfigFilePath(), $$"""
            { "CalendarMcp": { "Accounts": [
              { "Id": "plain", "TenantId": "{{TestData.TenantA}}", "DisplayName": "P", "Provider": "imap",
                "ProviderConfig": { "host": "h", "password": "hunter2" } },
              { "Id": "enc", "TenantId": "{{TestData.TenantA}}", "DisplayName": "E", "Provider": "imap",
                "ProviderConfig": { "host": "h", "password": "{{encrypted}}" } },
              { "Id": "dav", "TenantId": "{{TestData.TenantB}}", "DisplayName": "D", "Provider": "dav",
                "providerConfig": { "Password": "davpw" } },
              { "Id": "none", "TenantId": "{{TestData.TenantA}}", "DisplayName": "N", "Provider": "imap",
                "ProviderConfig": { "host": "h" } },
              { "Id": "legacy", "DisplayName": "L", "Provider": "ics" }
            ] } }
            """);
        var before = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath()))!;

        var count = await _service.ProtectStoredPasswordsAsync(protector);

        Assert.AreEqual(2, count);
        var after = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath()))!;
        var accounts = after["CalendarMcp"]!["Accounts"]!.AsArray();
        Assert.AreEqual("hunter2", protector.Unprotect(accounts[0]!["ProviderConfig"]!["password"]!.GetValue<string>()));
        Assert.IsTrue(PasswordProtector.IsProtected(accounts[0]!["ProviderConfig"]!["password"]!.GetValue<string>()));
        Assert.AreEqual("davpw", protector.Unprotect(accounts[2]!["providerConfig"]!["Password"]!.GetValue<string>()));
        Assert.IsTrue(PasswordProtector.IsProtected(accounts[2]!["providerConfig"]!["Password"]!.GetValue<string>()));
        var beforeAccounts = before["CalendarMcp"]!["Accounts"]!.AsArray();
        foreach (var i in new[] { 1, 3, 4 })
            Assert.AreEqual(beforeAccounts[i]!.ToJsonString(), accounts[i]!.ToJsonString());
        Assert.AreEqual("h", accounts[0]!["ProviderConfig"]!["host"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ProtectStored_LeavesEncryptedValuesAlone()
    {
        var protector = Protector();
        await File.WriteAllTextAsync(ConfigurationPaths.GetConfigFilePath(), $$"""
            { "CalendarMcp": { "Accounts": [
              { "Id": "plain", "TenantId": "{{TestData.TenantA}}", "DisplayName": "P", "Provider": "imap",
                "ProviderConfig": { "password": "hunter2" } }
            ] } }
            """);

        Assert.AreEqual(1, await _service.ProtectStoredPasswordsAsync(protector));
        var written = await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath());

        Assert.AreEqual(0, await _service.ProtectStoredPasswordsAsync(protector));
        Assert.AreEqual(written, await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath()));
    }

    [TestMethod]
    public async Task StartupPass_WithANewKeyRing_LeavesForeignCiphertextAlone()
    {
        var oldRing = Protector();
        await File.WriteAllTextAsync(ConfigurationPaths.GetConfigFilePath(), $$"""
            { "CalendarMcp": { "Accounts": [
              { "Id": "enc", "TenantId": "{{TestData.TenantA}}", "DisplayName": "E", "Provider": "imap",
                "ProviderConfig": { "password": "{{oldRing.Protect("secret")}}" } }
            ] } }
            """);
        var before = await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath());

        var count = await _service.ProtectStoredPasswordsAsync(Protector());

        Assert.AreEqual(0, count);
        Assert.AreEqual(before, await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath()));
    }

    [TestMethod]
    public async Task StartupPass_IsAtomic()
    {
        var path = ConfigurationPaths.GetConfigFilePath();
        await File.WriteAllTextAsync(path, $$"""
            { "CalendarMcp": { "Accounts": [
              { "Id": "plain", "TenantId": "{{TestData.TenantA}}", "DisplayName": "P", "Provider": "imap",
                "ProviderConfig": { "password": "hunter2" } }
            ] } }
            """);
        var before = await File.ReadAllTextAsync(path);
        var failing = new AccountConfigurationService(
            NullLogger<AccountConfigurationService>.Instance, _tenantContext,
            (_, _) => throw new IOException("disk full"));

        await Assert.ThrowsExactlyAsync<IOException>(() => failing.ProtectStoredPasswordsAsync(Protector()));

        Assert.AreEqual(before, await File.ReadAllTextAsync(path));
        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    [TestMethod]
    public async Task Write_LeavesNoTempFileBehind()
    {
        await _service.AddAccountAsync(TestData.CreateAccount(id: OwnId("fresh")));

        Assert.IsFalse(File.Exists(ConfigurationPaths.GetConfigFilePath() + ".tmp"));
    }

    [TestMethod]
    public async Task StartupPass_AFailureDoesNotStopTheHost()
    {
        await File.WriteAllTextAsync(ConfigurationPaths.GetConfigFilePath(), $$"""
            { "CalendarMcp": { "Accounts": [
              { "Id": "plain", "TenantId": "{{TestData.TenantA}}", "DisplayName": "P", "Provider": "imap",
                "ProviderConfig": { "password": "hunter2" } }
            ] } }
            """);
        var before = await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath());
        var failing = new AccountConfigurationService(
            NullLogger<AccountConfigurationService>.Instance, _tenantContext,
            (_, _) => throw new IOException("disk full"));
        var logs = new CapturingLogs();
        var host = new ProtectStoredPasswordsService(
            failing, Protector(), LoggerFactory.Create(b => b.AddProvider(logs)).CreateLogger<ProtectStoredPasswordsService>());

        await host.StartAsync(CancellationToken.None);

        Assert.AreEqual(1, logs.Entries.Count(entry => entry.Level == LogLevel.Error));
        Assert.AreEqual(before, await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath()));
    }

    [TestMethod]
    public async Task Write_AFailedMove_ReportsItsOwnFailure_EvenWhenTheTempFileCannotBeDeleted()
    {
        var path = ConfigurationPaths.GetConfigFilePath();
        var before = await File.ReadAllTextAsync(path);
        var failing = new AccountConfigurationService(
            NullLogger<AccountConfigurationService>.Instance, _tenantContext,
            (tmp, _) =>
            {
                File.Delete(tmp);
                Directory.CreateDirectory(tmp);
                throw new IOException("disk full");
            });

        var thrown = await Assert.ThrowsExactlyAsync<IOException>(
            () => failing.AddAccountAsync(TestData.CreateAccount(id: OwnId("fresh"))));

        Assert.AreEqual("disk full", thrown.Message);
        Assert.AreEqual(before, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task MergeProviderConfig_ReplacesOnlyTheProviderConfig()
    {
        var id = OwnId("mail");
        await _service.AddAccountAsync(TestData.CreateAccount(id: id, provider: "imap",
            providerConfig: new() { ["imapHost"] = "imap.example.com", ["password"] = "ENC:old" }));
        var stored = (await _service.GetAccountFromConfigAsync(id))!;
        var before = await StoredNodeAsync(id);

        await _service.MergeProviderConfigAsync(id, stored.ProviderConfig,
            new Dictionary<string, string> { ["imapHost"] = "imap.example.com", ["password"] = "ENC:new" });

        var after = await StoredNodeAsync(id);
        Assert.AreEqual("ENC:new", after["ProviderConfig"]!["password"]!.GetValue<string>());
        after.Remove("ProviderConfig");
        before.Remove("ProviderConfig");
        Assert.AreEqual(before.ToJsonString(), after.ToJsonString());
    }

    private static async Task<JsonObject> StoredNodeAsync(string id)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPaths.GetConfigFilePath()))!;
        return root["CalendarMcp"]!["Accounts"]!.AsArray()
            .Single(node => node!["Id"]!.GetValue<string>() == id)!.AsObject();
    }

    [TestMethod]
    public async Task MergeProviderConfig_OnAChangedNode_IsAConflict_AndWritesNothing()
    {
        var id = OwnId("mail");
        await _service.AddAccountAsync(TestData.CreateAccount(id: id, provider: "imap",
            providerConfig: new() { ["password"] = "ENC:old" }));
        var path = ConfigurationPaths.GetConfigFilePath();
        var before = await File.ReadAllTextAsync(path);

        await Assert.ThrowsExactlyAsync<ConcurrencyException>(() => _service.MergeProviderConfigAsync(id,
            new Dictionary<string, string> { ["password"] = "ENC:what-was-verified" },
            new Dictionary<string, string> { ["password"] = "ENC:new" }));

        Assert.AreEqual(before, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task MergeProviderConfig_OnAForeignAccount_IsNotFound()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _service.MergeProviderConfigAsync("foreign",
            new Dictionary<string, string>(), new Dictionary<string, string> { ["password"] = "ENC:new" }));
    }

    [TestMethod]
    public void KeyRing_LivesUnderTheDataDirectory()
    {
        StringAssert.StartsWith(
            ConfigurationPaths.GetDataProtectionKeysDirectory(), ConfigurationPaths.GetDataDirectory());
    }

    private static PasswordProtector Protector() => new(new EphemeralDataProtectionProvider());

    private static string OwnId(string localId) => TenantIdentity.AccountId(TestData.TenantA, localId);

    /// <summary>
    /// Replaces the config with one account owned by the bound tenant; a null
    /// <paramref name="permissionsJson"/> omits the Permissions block entirely.
    /// </summary>
    private static void WriteOwnAccount(string id, string provider, string? permissionsJson)
    {
        var permissions = permissionsJson is null ? "" : $"\"Permissions\": {permissionsJson},";
        File.WriteAllText(ConfigurationPaths.GetConfigFilePath(), $$"""
            {
              "CalendarMcp": {
                "Accounts": [
                  {
                    "Id": "{{id}}",
                    "TenantId": "{{TestData.TenantA}}",
                    "DisplayName": "{{id}}",
                    "Provider": "{{provider}}",
                    {{permissions}}
                    "ProviderConfig": {}
                  }
                ]
              }
            }
            """);
    }

    private static string InitialConfig() => $$"""
        {
          "CalendarMcp": {
            "Accounts": [
              {
                "Id": "own",
                "TenantId": "{{TestData.TenantA}}",
                "DisplayName": "Own",
                "Provider": "ics"
              },
              {
                "Id": "foreign",
                "TenantId": "{{TestData.TenantB}}",
                "DisplayName": "Foreign",
                "Provider": "ics"
              }
            ]
          }
        }
        """;
}
