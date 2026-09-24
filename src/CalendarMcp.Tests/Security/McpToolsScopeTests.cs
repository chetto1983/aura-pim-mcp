using System.Security.Claims;
using CalendarMcp.HttpServer.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarMcp.Tests.Security;

[TestClass]
public sealed class McpToolsScopeTests
{
    [TestMethod]
    [DataRow("mcp:tools", true)]
    [DataRow("openid mcp:tools profile", true)]
    [DataRow("profile:read", false)]
    [DataRow("mcp:tools:admin", false)]
    [DataRow("MCP:TOOLS", false)]
    [DataRow(null, false)]
    public async Task Policy_GrantsOnlyTheConfiguredScope(string? scope, bool granted)
    {
        var result = await Authorize(Bearer(scope, authenticationType: "Bearer"));

        Assert.AreEqual(granted, result.Succeeded);
    }

    [TestMethod]
    public async Task Policy_RejectsAnUnauthenticatedCallerEvenWithTheScopeClaim()
    {
        var result = await Authorize(Bearer("mcp:tools", authenticationType: null));

        Assert.IsFalse(result.Succeeded);
    }

    private static async Task<AuthorizationResult> Authorize(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationBuilder().AddMcpToolsPolicy("mcp:tools");
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, McpToolsScope.PolicyName);
    }

    private static ClaimsPrincipal Bearer(string? scope, string? authenticationType)
    {
        Claim[] claims = scope is null ? [] : [new Claim("scope", scope)];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }
}
