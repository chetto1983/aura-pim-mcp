using System.Text.Json;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.HttpServer.Admin;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CalendarMcp.Tests.Admin;

// Runs the real configuration pipeline (JSON file, reloadOnChange, IOptionsMonitor) because
// the defect is in its timing: without an explicit reload, the registry only learns of a
// write when the file watcher fires, 250-500 ms after the admin API has already answered.
[TestClass]
public sealed class AccountConfigurationReloadTests
{
    private DirectoryInfo _dir = null!;
    private string _path = null!;
    private ConfigurationRoot _configuration = null!;
    private ServiceProvider _services = null!;
    private AccountRegistry _registry = null!;
    private IDisposable _tenantBinding = null!;

    [TestInitialize]
    public void Start()
    {
        _dir = Directory.CreateTempSubdirectory("calendar-mcp-reload-");
        _path = Path.Combine(_dir.FullName, "appsettings.json");
        WriteAccounts("acc-1", "acc-2");

        _configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonFile(_path, optional: false, reloadOnChange: true)
            .Build();
        _services = new ServiceCollection()
            .AddOptions()
            .Configure<CalendarMcpConfiguration>(_configuration.GetSection("CalendarMcp"))
            .BuildServiceProvider();

        var tenant = new TenantContext();
        _tenantBinding = tenant.Bind(TestData.TenantA);
        _registry = new AccountRegistry(
            _services.GetRequiredService<IOptionsMonitor<CalendarMcpConfiguration>>(),
            NullLogger<AccountRegistry>.Instance,
            tenant);
    }

    [TestCleanup]
    public void Stop()
    {
        _registry.Dispose();
        _tenantBinding.Dispose();
        _services.Dispose();
        _configuration.Dispose();
        _dir.Delete(recursive: true);
    }

    [TestMethod]
    public async Task Reload_DropsARemovedAccountBeforeReturning()
    {
        WriteAccounts("acc-1");

        AccountConfigurationReload.Apply(_configuration);

        CollectionAssert.AreEquivalent(new[] { "acc-1" }, await AccountIds());
    }

    [TestMethod]
    public async Task Reload_ExposesAnAddedAccountBeforeReturning()
    {
        WriteAccounts("acc-1", "acc-2", "acc-3");

        AccountConfigurationReload.Apply(_configuration);

        CollectionAssert.AreEquivalent(new[] { "acc-1", "acc-2", "acc-3" }, await AccountIds());
    }

    private async Task<List<string>> AccountIds() =>
        (await _registry.GetAllAccountsAsync()).Select(a => a.Id).ToList();

    private void WriteAccounts(params string[] ids)
    {
        var accounts = ids.Select(id => new
        {
            Id = id,
            DisplayName = id,
            Provider = "google",
            TenantId = TestData.TenantA,
            Enabled = true,
        });
        File.WriteAllText(_path, JsonSerializer.Serialize(new { CalendarMcp = new { Accounts = accounts } }));
    }
}
