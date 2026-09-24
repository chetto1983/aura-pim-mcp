using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarMcp.Auth;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Tests.Helpers;
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
