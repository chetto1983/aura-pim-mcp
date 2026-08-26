using System.Security.Claims;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Tests.Helpers;

namespace CalendarMcp.Tests.Tenancy;

[TestClass]
public sealed class TenantContextTests
{
    [TestMethod]
    public void FromPrincipal_ReadsAndNormalizesOAuthSubject()
    {
        Assert.AreEqual(TestData.TenantA, TenantIdentity.FromPrincipal(Principal(TestData.TenantA.ToUpperInvariant())));
    }

    [TestMethod]
    public void FromPrincipal_RejectsMissingOrInvalidSubject()
    {
        Assert.ThrowsExactly<ArgumentException>(() => TenantIdentity.FromPrincipal(null));
        Assert.ThrowsExactly<ArgumentException>(() => TenantIdentity.FromPrincipal(Principal("not-a-uuid")));
    }

    [TestMethod]
    public void Bind_IsNestedAndFailClosedOutsideScope()
    {
        var context = new TenantContext();
        Assert.ThrowsExactly<InvalidOperationException>(() => context.RequireTenantId());

        using (context.Bind(TestData.TenantA))
        {
            Assert.AreEqual(TestData.TenantA, context.RequireTenantId());
            using (context.Bind(TestData.TenantB))
                Assert.AreEqual(TestData.TenantB, context.RequireTenantId());
            Assert.AreEqual(TestData.TenantA, context.RequireTenantId());
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => context.RequireTenantId());
    }

    [TestMethod]
    public void AccountId_IsDifferentForSameSlugAcrossTenants()
    {
        var first = TenantIdentity.AccountId(TestData.TenantA, "work");
        var second = TenantIdentity.AccountId(TestData.TenantB, "work");

        Assert.AreNotEqual(first, second);
        Assert.IsTrue(first.EndsWith("__work", StringComparison.Ordinal));
        Assert.IsTrue(second.EndsWith("__work", StringComparison.Ordinal));
    }

    private static ClaimsPrincipal Principal(string subject) => new(new ClaimsIdentity(
        [new Claim(TenantIdentity.OAuthClaimName, subject)], "Bearer"));
}
