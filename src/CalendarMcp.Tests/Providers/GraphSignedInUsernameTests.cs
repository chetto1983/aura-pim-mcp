using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// The sending address shown for a Microsoft account is the username of the MSAL account its tokens come from. MSAL
/// keeps one application per tenant, client and account id, so the username read must name the same three values
/// as the token read, or it reads another sign-in (the shape of 93dfa28, where an account added again under another
/// tenant kept the first application).
/// </summary>
[TestClass]
public class GraphSignedInUsernameTests
{
    private const string AccountId = "tenant__microsoft";
    private const string ClientId = "11111111-2222-3333-4444-555555555555";

    private static SingleAccountRegistry Registry(string tenantId) => new(
        TestData.CreateAccount(id: AccountId, providerConfig: new Dictionary<string, string>
        {
            ["tenantId"] = tenantId,
            ["clientId"] = ClientId
        }));

    private static IEnumerable<(string Tenant, IGraphApiClients Provider, StaticTokenAuthentication Auth)> Providers()
    {
        var work = new StaticTokenAuthentication();
        yield return ("contoso-tenant", new M365ProviderService(
            NullLogger<M365ProviderService>.Instance, work, Registry("contoso-tenant")), work);
        var personal = new StaticTokenAuthentication();
        yield return ("consumers", new OutlookComProviderService(
            NullLogger<OutlookComProviderService>.Instance, personal, Registry("consumers")), personal);
    }

    [TestMethod]
    public async Task TheUsernameAndTheTokenReachTheSameMsalApplication()
    {
        foreach (var (tenant, provider, auth) in Providers())
        {
            await provider.GraphAsync(AccountId, default);
            await provider.SignedInUsernameAsync(AccountId);

            var expected = (tenant, ClientId, AccountId);
            var token = auth.Reads.Single(read => read.Read == "token");
            var username = auth.Reads.Single(read => read.Read == "username");
            Assert.AreEqual(expected, (token.TenantId, token.ClientId, token.AccountId), provider.GetType().Name);
            Assert.AreEqual(expected, (username.TenantId, username.ClientId, username.AccountId), provider.GetType().Name);
        }
    }
}
