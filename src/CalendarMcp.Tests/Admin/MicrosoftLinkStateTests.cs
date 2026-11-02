using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.HttpServer.Admin;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Admin;

// An account exists from the moment it is added, long before anyone signs in. "linked" is what
// tells the two apart, so a failed sign-in must not read as a connected account.
[TestClass]
[DoNotParallelize]
public sealed class MicrosoftLinkStateTests
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";

    private string _directory = null!;
    private string? _previousConfig;
    private M365AuthenticationService _service = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "calendar-mcp-link-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _previousConfig = Environment.GetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable);
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _directory);
        _service = new M365AuthenticationService(NullLogger<M365AuthenticationService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _previousConfig);
        Directory.Delete(_directory, recursive: true);
    }

    private static AccountInfo Outlook(string? clientId = ClientId, string provider = "outlook.com")
    {
        var config = new Dictionary<string, string> { ["tenantId"] = "consumers" };
        if (clientId is not null)
            config["clientId"] = clientId;
        return TestData.CreateAccount(id: "tenant__outlook", provider: provider, providerConfig: config);
    }

    [TestMethod]
    public async Task AccountThatNeverFinishedSigningIn_IsNotLinked()
    {
        Assert.IsFalse(await AdminEndpoints.LinkedAsync(Outlook(), _service));
    }

    [TestMethod]
    [DataRow("microsoft365")]
    [DataRow("m365")]
    [DataRow("outlook.com")]
    [DataRow("outlook")]
    [DataRow("hotmail")]
    public async Task AccountHoldingASignedInUser_IsLinked(string provider)
    {
        Assert.IsTrue(await AdminEndpoints.LinkedAsync(Outlook(provider: provider), new StaticTokenAuthentication()));
    }

    [TestMethod]
    public async Task AccountWithoutAClient_CannotHaveSignedIn()
    {
        Assert.IsFalse(await AdminEndpoints.LinkedAsync(Outlook(clientId: null), new StaticTokenAuthentication()));
    }

    [TestMethod]
    public async Task AppMsalRefuses_ReadsAsNotLinkedInsteadOfFailingTheList()
    {
        Assert.IsFalse(await AdminEndpoints.LinkedAsync(Outlook(clientId: "not a guid"), _service));
    }

    [TestMethod]
    public async Task GoogleKeepsItsOwnState_AndAFeedHasNothingToLink()
    {
        var google = TestData.CreateAccount(id: "tenant__google", provider: "google");
        var feed = TestData.CreateAccount(id: "tenant__feed", provider: "ics");

        Assert.IsFalse(await AdminEndpoints.LinkedAsync(google, _service));
        Assert.IsNull(await AdminEndpoints.LinkedAsync(feed, _service));
    }

    // Measured on 2026-11-02: the account's file said consumers while Microsoft was asked for
    // common, because the service kept the first application it built under the account's id.
    [TestMethod]
    public async Task AccountAddedAgainWithAnotherTenant_SignsInThroughTheNewOne()
    {
        var first = await _service.GetOrCreateAppAsync("common", ClientId, "tenant__outlook");
        var second = await _service.GetOrCreateAppAsync("consumers", ClientId, "tenant__outlook");

        StringAssert.Contains(first.Authority, "/common");
        StringAssert.Contains(second.Authority, "/consumers");
    }

    [TestMethod]
    public async Task SameConfiguration_KeepsTheApplication()
    {
        var first = await _service.GetOrCreateAppAsync("consumers", ClientId, "tenant__outlook");
        var second = await _service.GetOrCreateAppAsync("consumers", ClientId, "tenant__outlook");

        Assert.AreSame(first, second);
    }
}
