using System.Text.Json;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tools;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Rocks;

namespace CalendarMcp.Tests.Tools;

[TestClass]
public class CreateContactToolTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task CreateContact_WithoutAnAccount_IsRefusedAndReachesNoProvider(string accountId)
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new CreateContactTool(regExp.Instance(), factExp.Instance(),
            NullLogger<CreateContactTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.CreateContact("Ada Lovelace", accountId));

        StringAssert.Contains(ex.Message, "accountId");
        StringAssert.Contains(ex.Message, "list_accounts");
        regExp.Verify();
        factExp.Verify();
    }

    [TestMethod]
    public async Task CreateContact_AccountWithoutContactsWrite_IsRefused()
    {
        var account = TestData.CreateAccount(
            id: "read-only", provider: "google",
            permissions: AccountPermissions.None.With(AccountPermission.ContactsRead, true));
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("read-only").ReturnValue(Task.FromResult<AccountInfo?>(account));
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new CreateContactTool(regExp.Instance(), factExp.Instance(),
            NullLogger<CreateContactTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.CreateContact("Ada Lovelace", "read-only"));

        StringAssert.Contains(ex.Message, "does not permit");
        regExp.Verify();
        factExp.Verify();
    }

    [TestMethod]
    public async Task CreateContact_WithAWritableAccount_CreatesThere()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "google");
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1").ReturnValue(Task.FromResult<AccountInfo?>(account));
        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.CreateContactAsync(
                "acc-1", "Ada Lovelace", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<List<string>?>(),
                Arg.Any<List<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult("contact-1"));
        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("google").ReturnValue(provExp.Instance());
        var tool = new CreateContactTool(regExp.Instance(), factExp.Instance(),
            NullLogger<CreateContactTool>.Instance);

        var doc = JsonDocument.Parse(await tool.CreateContact("Ada Lovelace", "acc-1"));

        Assert.AreEqual("contact-1", doc.RootElement.GetProperty("contactId").GetString());
        Assert.AreEqual("acc-1", doc.RootElement.GetProperty("accountUsed").GetString());
        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }
}
