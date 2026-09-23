using CalendarMcp.Core.Configuration;
using CalendarMcp.HttpServer.Admin;
using CalendarMcp.Tests.Helpers;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;

namespace CalendarMcp.Tests.Admin;

// "linked" must read the very file Google's FileDataStore writes after the code exchange, so
// the tests store a token through the library itself rather than creating a file by name.
[TestClass]
[DoNotParallelize]
public sealed class GoogleLinkStateTests
{
    private string _directory = null!;
    private string? _previousConfig;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "calendar-mcp-link-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _previousConfig = Environment.GetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable);
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _previousConfig);
        Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task GoogleAccount_IsLinkedOnceTheExchangeStoredItsToken()
    {
        var account = TestData.CreateAccount(id: "tenant__google", provider: "google");
        Assert.IsFalse(AdminEndpoints.GoogleLinked(account));

        var store = new FileDataStore(ConfigurationPaths.GetGoogleCredentialsDirectory(account.Id), true);
        await store.StoreAsync("user", new TokenResponse { AccessToken = "a", RefreshToken = "r" });

        Assert.IsTrue(AdminEndpoints.GoogleLinked(account));
    }

    [TestMethod]
    public void NonGoogleAccount_HasNoOfflineLinkState()
    {
        Assert.IsNull(AdminEndpoints.GoogleLinked(TestData.CreateAccount(id: "tenant__work", provider: "microsoft365")));
        Assert.IsNull(AdminEndpoints.GoogleLinked(TestData.CreateAccount(id: "tenant__feed", provider: "ics")));
    }
}
