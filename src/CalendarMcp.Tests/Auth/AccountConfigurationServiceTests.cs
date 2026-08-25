using System.Text.Json.Nodes;
using CalendarMcp.Auth;
using CalendarMcp.Core.Configuration;
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
        var id = TenantIdentity.AccountId(TestData.TenantA, "new-account");
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
