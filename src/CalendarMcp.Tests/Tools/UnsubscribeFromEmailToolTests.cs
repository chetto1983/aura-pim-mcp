using System.Text.Json;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tools;
using CalendarMcp.Core.Utilities;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Rocks;

namespace CalendarMcp.Tests.Tools;

[TestClass]
public class UnsubscribeFromEmailToolTests
{
    // UnsubscribeExecutor requires IHttpClientFactory - create a real instance for validation tests
    private static UnsubscribeFromEmailTool CreateTool(
        IAccountRegistry registry, IProviderServiceFactory factory)
    {
        var httpFactory = new TestHttpClientFactory();
        var executor = new UnsubscribeExecutor(httpFactory, NullLogger<UnsubscribeExecutor>.Instance);
        return new UnsubscribeFromEmailTool(registry, factory, executor,
            NullLogger<UnsubscribeFromEmailTool>.Instance);
    }

    [TestMethod]
    public async Task UnsubscribeFromEmail_EmptyAccountId_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = CreateTool(regExp.Instance(), factExp.Instance());

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.UnsubscribeFromEmail("", "email-1"));
        Assert.AreEqual("accountId is required", ex.Message);
    }

    [TestMethod]
    public async Task UnsubscribeFromEmail_EmptyEmailId_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = CreateTool(regExp.Instance(), factExp.Instance());

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.UnsubscribeFromEmail("acc-1", ""));
        Assert.AreEqual("emailId is required", ex.Message);
    }

    [TestMethod]
    public async Task UnsubscribeFromEmail_AccountNotFound_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("nonexistent")
            .ReturnValue(Task.FromResult<AccountInfo?>(null));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = CreateTool(regExp.Instance(), factExp.Instance());

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.UnsubscribeFromEmail("nonexistent", "email-1"));
        Assert.AreEqual("Account 'nonexistent' not found", ex.Message);
        regExp.Verify();
    }

    [TestMethod]
    public async Task UnsubscribeFromEmail_EmailNotFound_ThrowsMcpException()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetEmailDetailsAsync("acc-1", "missing", Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<EmailMessage?>(null));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = CreateTool(regExp.Instance(), factExp.Instance());

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.UnsubscribeFromEmail("acc-1", "missing"));
        Assert.IsTrue(ex.Message.Contains("not found"));
        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task UnsubscribeFromEmail_NoUnsubscribeHeaders_ReturnsFalse()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");
        var email = TestData.CreateEmail(id: "e1", accountId: "acc-1");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetEmailDetailsAsync("acc-1", "e1", Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<EmailMessage?>(email));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = CreateTool(regExp.Instance(), factExp.Instance());

        var result = await tool.UnsubscribeFromEmail("acc-1", "e1");
        var doc = JsonDocument.Parse(result);

        Assert.IsFalse(doc.RootElement.GetProperty("success").GetBoolean());

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task UnsubscribeByMail_ASendThatFails_IsDescribedWithoutTheProvidersOwnText()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "imap");
        var email = new EmailMessage
        {
            Id = "e1",
            AccountId = "acc-1",
            UnsubscribeInfo = new UnsubscribeInfo { MailtoUrl = "mailto:unsub@example.com" }
        };

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1").ReturnValue(Task.FromResult<AccountInfo?>(account));
        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetEmailDetailsAsync("acc-1", "e1", Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<EmailMessage?>(email));
        provExp.Setups.SendEmailAsync("acc-1", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<List<string>?>(), Arg.Any<IReadOnlyList<OutboundEmailAttachment>?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromException<string>(
                new PrivateAddressException("smtp.example.org", System.Net.IPAddress.Parse("10.0.0.5"))));
        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("imap").ReturnValue(provExp.Instance());

        var result = await CreateTool(regExp.Instance(), factExp.Instance()).UnsubscribeFromEmail("acc-1", "e1", "mailto");

        var details = JsonDocument.Parse(result).RootElement.GetProperty("ErrorDetails").GetString()!;
        StringAssert.Contains(details, "private network address");
        Assert.DoesNotContain("10.0.0.5", details, "where a name resolves inside the PIM's network is not the caller's");
        provExp.Verify();
    }

    /// <summary>Minimal IHttpClientFactory for tests</summary>
    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
