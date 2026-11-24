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
public class SendEmailToolTests
{
    [TestMethod]
    public async Task SendEmail_SpecificAccount_Success()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.SendEmailAsync(
            "acc-1", "to@example.com", "Subject", "Body", Arg.Any<string>(),
            Arg.Any<List<string>?>(), Arg.Any<IReadOnlyList<OutboundEmailAttachment>?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult("msg-123"));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new SendEmailTool(regExp.Instance(), factExp.Instance(),
            new TestAttachmentStore(), NullLogger<SendEmailTool>.Instance);

        var result = await tool.SendEmail("acc-1", new List<string> { "to@example.com" }, "Subject", "Body");
        var doc = JsonDocument.Parse(result);

        Assert.IsTrue(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual("msg-123", doc.RootElement.GetProperty("messageId").GetString());

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task SendEmail_AccountNotFound_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("nonexistent")
            .ReturnValue(Task.FromResult<AccountInfo?>(null));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new SendEmailTool(regExp.Instance(), factExp.Instance(),
            new TestAttachmentStore(), NullLogger<SendEmailTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.SendEmail("nonexistent", new List<string> { "to@example.com" }, "Subject", "Body"));
        Assert.AreEqual("Account 'nonexistent' not found", ex.Message);
        regExp.Verify();
    }

    private const string AccountRequired =
        "send_email requires accountId, the account to send from: call list_accounts and pass its accountId. " +
        "When the user has more than one account and has not named one, ask which.";

    [TestMethod]
    public void SendEmail_SchemaRequiresAccountId()
    {
        var method = typeof(SendEmailTool).GetMethod(nameof(SendEmailTool.SendEmail))!;
        var tool = ModelContextProtocol.Server.McpServerTool.Create(method, _ => null!);

        var required = tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToList();

        CollectionAssert.Contains(required, "accountId");
    }

    [TestMethod]
    public void SendEmail_DescriptionsDoNotOfferRouting()
    {
        var method = typeof(SendEmailTool).GetMethod(nameof(SendEmailTool.SendEmail))!;
        var tool = ModelContextProtocol.Server.McpServerTool.Create(method, _ => null!);
        var account = method.GetParameters().Single(p => p.Name == "accountId")
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        foreach (var text in new[] { tool.ProtocolTool.Description!, account })
        {
            Assert.DoesNotContain("smart routing", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("omit", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task SendEmail_WithoutAnAccount_IsRefusedAndNothingIsSent(string accountId)
    {
        var tool = new SendEmailTool(new IAccountRegistryCreateExpectations().Instance(),
            new IProviderServiceFactoryCreateExpectations().Instance(),
            new TestAttachmentStore(), NullLogger<SendEmailTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.SendEmail(accountId: accountId, to: ["to@example.com"], subject: "Subject", body: "Body"));

        Assert.AreEqual(AccountRequired, ex.Message);
    }

    [TestMethod]
    public async Task SendEmail_WithoutAccountId_NamesItAndReachesNoProvider()
    {
        var factory = new RecordingProviderFactory();

        var result = await SurfaceSession.CallAsync(factory, "send_email", """{"to":["x@example.org"],"subject":"s"}""");

        Assert.IsTrue(result.IsError, SurfaceSession.Text(result));
        StringAssert.Contains(SurfaceSession.Text(result), "Required parameter 'accountId' was not provided to 'send_email'.");
        Assert.IsEmpty(factory.Requested);
    }

    // Measured 2026-11-24: a JSON null for body or bodyFormat reached the tool as null, past the defaults.
    [TestMethod]
    public async Task SendEmail_NullBodyAndFormat_SendAnEmptyHtmlBody()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(TestData.CreateAccount(id: "acc-1", provider: "microsoft365")));
        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.SendEmailAsync(
            "acc-1", "to@example.com", "Subject", "", "html",
            Arg.Any<List<string>?>(), Arg.Any<IReadOnlyList<OutboundEmailAttachment>?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult("msg-123"));
        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());
        var tool = new SendEmailTool(regExp.Instance(), factExp.Instance(),
            new TestAttachmentStore(), NullLogger<SendEmailTool>.Instance);

        await tool.SendEmail("acc-1", ["to@example.com"], "Subject", body: null!, bodyFormat: null!);

        provExp.Verify();
    }

    [TestMethod]
    public async Task SendEmail_EmptyToList_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new SendEmailTool(regExp.Instance(), factExp.Instance(),
            new TestAttachmentStore(), NullLogger<SendEmailTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.SendEmail("acc-1", [], "Subject", "Body"));
        Assert.IsTrue(ex.Message.Contains("recipient", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SendEmail_WithAttachment_PassesThroughToProvider()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.SendEmailAsync(
            "acc-1", "to@example.com", "Subject", "Body", Arg.Any<string>(),
            Arg.Any<List<string>?>(), Arg.Any<IReadOnlyList<OutboundEmailAttachment>?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult("msg-123"));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new SendEmailTool(regExp.Instance(), factExp.Instance(),
            new TestAttachmentStore(), NullLogger<SendEmailTool>.Instance);

        var attachment = new OutboundEmailAttachment
        {
            Name = "hello.txt",
            ContentType = "text/plain",
            Base64Content = Convert.ToBase64String("hello"u8.ToArray()),
        };

        var result = await tool.SendEmail(
            "acc-1", new List<string> { "to@example.com" }, "Subject", "Body",
            attachments: new List<OutboundEmailAttachment> { attachment });

        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.GetProperty("success").GetBoolean());

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task SendEmail_AttachmentMissingName_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new SendEmailTool(regExp.Instance(), factExp.Instance(),
            new TestAttachmentStore(), NullLogger<SendEmailTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.SendEmail(
                "acc-1", new List<string> { "to@example.com" }, "Subject", "Body",
                attachments: new List<OutboundEmailAttachment>
                {
                    new() { Name = "", Base64Content = "aGVsbG8=" },
                }));
        Assert.IsTrue(ex.Message.Contains("name is required", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SendEmail_AttachmentMissingContent_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new SendEmailTool(regExp.Instance(), factExp.Instance(),
            new TestAttachmentStore(), NullLogger<SendEmailTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.SendEmail(
                "acc-1", new List<string> { "to@example.com" }, "Subject", "Body",
                attachments: new List<OutboundEmailAttachment>
                {
                    new() { Name = "x.txt", Base64Content = "" },
                }));
        Assert.IsTrue(ex.Message.Contains("base64Content", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SendEmail_AttachmentExceedsTotalCap_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new SendEmailTool(regExp.Instance(), factExp.Instance(),
            new TestAttachmentStore(), NullLogger<SendEmailTool>.Instance);

        // 35 MB of base64 chars decodes to ~26 MB, just over the 25 MB cap.
        var bigBase64 = new string('A', 35 * 1024 * 1024);
        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.SendEmail(
                "acc-1", new List<string> { "to@example.com" }, "Subject", "Body",
                attachments: new List<OutboundEmailAttachment>
                {
                    new() { Name = "a.bin", Base64Content = bigBase64 },
                }));
        Assert.IsTrue(ex.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
    }
}
