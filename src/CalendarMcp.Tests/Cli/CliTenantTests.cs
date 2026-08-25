using System.Text.Json;
using CalendarMcp.Cli.Commands;

namespace CalendarMcp.Tests.Cli;

[TestClass]
public sealed class CliTenantTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    [TestMethod]
    public void AccountIdPrefixesTheValidatedIdentity()
    {
        Assert.AreEqual("11111111111111111111111111111111__work", CliTenant.AccountId(" work ", TenantA));
        Assert.ThrowsExactly<ArgumentException>(() => CliTenant.AccountId(" ", TenantA));
        Assert.ThrowsExactly<ArgumentException>(() => CliTenant.AccountId("work", "not-an-identity"));
    }

    [TestMethod]
    public void DictionaryOwnershipIsFailClosed()
    {
        var owned = Account("TenantId", TenantA);
        var foreign = Account("tenantId", TenantB);
        var missing = new Dictionary<string, JsonElement>();
        var malformed = Account("TenantId", "not-an-identity");

        Assert.IsTrue(CliTenant.Owns(owned, TenantA));
        Assert.IsFalse(CliTenant.Owns(foreign, TenantA));
        Assert.IsFalse(CliTenant.Owns(missing, TenantA));
        Assert.IsFalse(CliTenant.Owns(malformed, TenantA));
    }

    [TestMethod]
    public void JsonOwnershipAndAccountLookupSupportBothConfigCasings()
    {
        using var document = JsonDocument.Parse($$"""
            { "tenantId": "{{TenantA}}", "id": "account-a" }
            """);

        Assert.IsTrue(CliTenant.Owns(document.RootElement, TenantA));
        Assert.IsFalse(CliTenant.Owns(document.RootElement, TenantB));
        Assert.IsTrue(CliTenant.HasAccountId(
            new Dictionary<string, object> { ["Id"] = "account-a" }, "account-a"));
    }

    private static Dictionary<string, JsonElement> Account(string key, string value)
    {
        using var document = JsonDocument.Parse($$"""{ "{{key}}": "{{value}}" }""");
        return document.RootElement.Deserialize<Dictionary<string, JsonElement>>()!;
    }
}
