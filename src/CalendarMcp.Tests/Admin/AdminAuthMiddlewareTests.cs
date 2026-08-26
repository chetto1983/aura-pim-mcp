using System.Security.Claims;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.HttpServer.Admin;
using CalendarMcp.Tests.Helpers;
using Microsoft.AspNetCore.Http;

namespace CalendarMcp.Tests.Admin;

[TestClass]
public sealed class AdminAuthMiddlewareTests
{
    [TestMethod]
    public async Task AuthenticatedSubject_BindsTenantForRequest()
    {
        var tenantContext = new TenantContext();
        string? seenTenant = null;
        var middleware = CreateMiddleware(_ =>
        {
            seenTenant = tenantContext.RequireTenantId();
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Request(TestData.TenantA), tenantContext);

        Assert.AreEqual(TestData.TenantA, seenTenant);
        Assert.ThrowsExactly<InvalidOperationException>(() => tenantContext.RequireTenantId());
    }

    [TestMethod]
    public async Task MissingSubject_FailsBeforeHandler()
    {
        var called = false;
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var missing = Request(null);
        await middleware.InvokeAsync(missing, new TenantContext());
        Assert.AreEqual(StatusCodes.Status401Unauthorized, missing.Response.StatusCode);
        Assert.IsFalse(called);
    }

    [TestMethod]
    public async Task UnauthenticatedOrWrongScope_FailsClosed()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var anonymous = new DefaultHttpContext();
        anonymous.Response.Body = new MemoryStream();
        anonymous.Request.Path = "/admin/accounts";
        await middleware.InvokeAsync(anonymous, new TenantContext());
        Assert.AreEqual(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);

        var wrongScope = Request(TestData.TenantA, "profile:read");
        await middleware.InvokeAsync(wrongScope, new TenantContext());
        Assert.AreEqual(StatusCodes.Status403Forbidden, wrongScope.Response.StatusCode);
    }

    [TestMethod]
    public async Task GoogleCallback_RemainsBearerAndTenantExempt()
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

    private static AdminAuthMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next);

    private static DefaultHttpContext Request(string? subject, string scope = "mcp:tools")
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.Request.Path = "/admin/accounts";
        var claims = new List<Claim> { new("scope", scope) };
        if (subject is not null)
            claims.Add(new Claim(TenantIdentity.OAuthClaimName, subject));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return http;
    }
}
