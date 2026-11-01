using CalendarMcp.Auth;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Services;
using CalendarMcp.HttpServer.Admin;
using CalendarMcp.Tests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class ChangesRegistrationTests
{
    [TestMethod]
    public void EveryProviderOfTheContract_HasItsSources()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<CalendarMcpConfiguration>>(
            new TestOptionsMonitor<CalendarMcpConfiguration>(new CalendarMcpConfiguration()));
        services.AddCalendarMcpCore();
        using var provider = services.BuildServiceProvider();

        var kinds = provider.GetServices<IChangesSource>()
            .Select(source => ChangesCursor.KindOf(source.Provider, source.Resource))
            .Order()
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "google.calendar", "google.mail", "imap.mail",
                "microsoft365.calendar", "microsoft365.mail", "outlook.calendar", "outlook.mail"
            },
            kinds);
        Assert.IsNotNull(provider.GetRequiredService<ChangesService>());
    }

    [TestMethod]
    public void TheAdminApi_ServesBothRoutes()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IOptionsMonitor<CalendarMcpConfiguration>>(
            new TestOptionsMonitor<CalendarMcpConfiguration>(new CalendarMcpConfiguration()));
        builder.Services.AddCalendarMcpCore();
        builder.Services.AddSingleton<IAccountConfigurationService, AccountConfigurationService>();
        builder.Services.AddSingleton<DeviceCodeAuthManager>();
        builder.Services.AddSingleton<GoogleOAuthManager>();
        using var app = builder.Build();

        app.MapAdminEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();
        CollectionAssert.Contains(routes, "/admin/changes");
        CollectionAssert.Contains(routes, "/admin/items");
    }

    [TestMethod]
    [DataRow("google", "google")]
    [DataRow("Gmail", "google")]
    [DataRow("google workspace", "google")]
    [DataRow("microsoft365", "microsoft365")]
    [DataRow("m365", "microsoft365")]
    [DataRow("outlook.com", "outlook")]
    [DataRow("hotmail", "outlook")]
    [DataRow("imap", "imap")]
    [DataRow("imap-smtp", "imap")]
    [DataRow("ics", null)]
    [DataRow("json", null)]
    [DataRow("dav", null)]
    [DataRow("caldav", null)]
    public void AnAccountsProvider_NamesItsFamily(string provider, string? family)
    {
        Assert.AreEqual(family, ChangesProviders.Family(provider));
    }
}
