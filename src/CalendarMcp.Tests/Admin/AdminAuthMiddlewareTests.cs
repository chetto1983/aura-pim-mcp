using CalendarMcp.Core.Tenancy;
using CalendarMcp.HttpServer.Admin;
using CalendarMcp.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Admin;

[TestClass]
public sealed class AdminAuthMiddlewareTests
{
    private const string Token = "calendar-test-token-long-enough";

    [TestMethod]
    public async Task ValidTokenAndAuraIdentity_BindTenantForRequest()
    {
        var tenantContext = new TenantContext();
        string? seenTenant = null;
        var middleware = CreateMiddleware(_ =>
        {
            seenTenant = tenantContext.RequireTenantId();
            return Task.CompletedTask;
        });
        var http = Request("/admin/accounts", includeIdentity: true);

        await middleware.InvokeAsync(http, tenantContext);

        Assert.AreEqual(TestData.TenantA, seenTenant);
        Assert.ThrowsExactly<InvalidOperationException>(() => tenantContext.RequireTenantId());
    }

    [TestMethod]
    public async Task MissingIdentity_FailsClosedBeforeHandler()
    {
        var called = false;
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var http = Request("/admin/accounts", includeIdentity: false);

        await middleware.InvokeAsync(http, new TenantContext());

        Assert.AreEqual(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.IsFalse(called);
    }

    [TestMethod]
    public async Task MissingConfiguredToken_FailsClosed()
    {
        var config = new ConfigurationBuilder().Build();
        var middleware = new AdminAuthMiddleware(
            _ => Task.CompletedTask, config, NullLogger<AdminAuthMiddleware>.Instance);
        var http = Request("/admin/accounts", includeIdentity: true);

        await middleware.InvokeAsync(http, new TenantContext());

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
    }

    [TestMethod]
    public async Task GoogleCallback_RemainsTokenAndTenantExempt()
    {
        var called = false;
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var http = new DefaultHttpContext();
        http.Request.Path = "/admin/auth/google/callback";

        await middleware.InvokeAsync(http, new TenantContext());

        Assert.IsTrue(called);
    }

    [TestMethod]
    public async Task McpServiceAuth_RequiresConfiguredValidBearer()
    {
        var called = false;
        var config = Configuration();
        var middleware = new McpServiceAuthMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            config,
            NullLogger<McpServiceAuthMiddleware>.Instance);
        var unauthorized = new DefaultHttpContext();
        unauthorized.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(unauthorized);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, unauthorized.Response.StatusCode);
        Assert.IsFalse(called);

        var authorized = new DefaultHttpContext();
        authorized.Request.Headers.Authorization = "Bearer " + Token;
        await middleware.InvokeAsync(authorized);
        Assert.IsTrue(called);
    }

    private static AdminAuthMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new AdminAuthMiddleware(next, Configuration(), NullLogger<AdminAuthMiddleware>.Instance);
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CalendarMcp:AdminToken"] = Token
            })
            .Build();

    private static DefaultHttpContext Request(string path, bool includeIdentity)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.Request.Path = path;
        http.Request.Headers.Authorization = "Bearer " + Token;
        if (includeIdentity)
            http.Request.Headers["X-Aura-Identity"] = TestData.TenantA;
        return http;
    }
}
