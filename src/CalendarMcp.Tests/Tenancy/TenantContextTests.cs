using System.Text.Json.Nodes;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Tests.Helpers;

namespace CalendarMcp.Tests.Tenancy;

[TestClass]
public sealed class TenantContextTests
{
    [TestMethod]
    public void FromMcpMeta_ReadsAndNormalizesAuraIdentity()
    {
        var meta = new JsonObject
        {
            ["aura"] = new JsonObject { ["user_identifier"] = TestData.TenantA.ToUpperInvariant() }
        };

        Assert.AreEqual(TestData.TenantA, TenantIdentity.FromMcpMeta(meta));
    }

    [TestMethod]
    public void FromMcpMeta_RejectsMissingOrInvalidIdentity()
    {
        Assert.ThrowsExactly<ArgumentException>(() => TenantIdentity.FromMcpMeta(null));
        Assert.ThrowsExactly<ArgumentException>(() => TenantIdentity.FromMcpMeta(
            new JsonObject { ["aura"] = new JsonObject { ["user_identifier"] = "not-a-uuid" } }));
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
}
