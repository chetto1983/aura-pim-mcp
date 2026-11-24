using System.Text.Json;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tools;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Rocks;
using CalendarMcp.Core.Tenancy;

namespace CalendarMcp.Tests.Tools;

[TestClass]
public class GetEmailAttachmentToolTests
{
    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;

    [TestMethod]
    public async Task StashMode_DefaultsAndStoresInAttachmentStore()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");
        var bytes = "PDF-content"u8.ToArray();
        var (regExp, factExp, provExp) = WireProviderForFetch(
            account, "msg-1", "graph-att-1",
            new EmailAttachmentContent { Name = "report.pdf", ContentType = "application/pdf", Bytes = bytes });

        var tenantContext = new TenantContext();
        using var tenantBinding = tenantContext.Bind(TestData.TenantA);
        var store = new InMemoryAttachmentStore(
            Options.Create(new AttachmentStoreOptions()),
            NullLogger<InMemoryAttachmentStore>.Instance,
            tenantContext);

        var tool = new GetEmailAttachmentTool(regExp.Instance(), factExp.Instance(), store, Options.Create(new AttachmentStoreOptions()),
            NullLogger<GetEmailAttachmentTool>.Instance);

        var json = Text(await tool.GetEmailAttachment("acc-1", "msg-1", "graph-att-1"));

        var doc = JsonDocument.Parse(json).RootElement;
        var newId = doc.GetProperty("attachmentId").GetString()!;
        Assert.IsFalse(string.IsNullOrEmpty(newId));
        Assert.AreEqual("report.pdf", doc.GetProperty("name").GetString());
        Assert.AreEqual("application/pdf", doc.GetProperty("contentType").GetString());
        Assert.AreEqual(bytes.Length, doc.GetProperty("size").GetInt64());

        // The store should now have the bytes under the returned ID,
        // ready to feed into send_email.
        var stored = store.TryConsume(newId);
        Assert.IsNotNull(stored);
        CollectionAssert.AreEqual(bytes, stored!.Bytes);

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task GetEmailAttachment_AnswersWithAResourceLink()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");
        var (regExp, factExp, _) = WireProviderForFetch(
            account, "msg-1", "att-1",
            new EmailAttachmentContent { Name = "scan.pdf", ContentType = "application/octet-stream", Bytes = [1, 2, 3] });
        var tenantContext = new TenantContext();
        using var tenantBinding = tenantContext.Bind(TestData.TenantA);
        var store = new InMemoryAttachmentStore(
            Options.Create(new AttachmentStoreOptions()),
            NullLogger<InMemoryAttachmentStore>.Instance,
            tenantContext);
        var tool = new GetEmailAttachmentTool(regExp.Instance(), factExp.Instance(), store, Options.Create(new AttachmentStoreOptions()),
            NullLogger<GetEmailAttachmentTool>.Instance);

        object answer = await tool.GetEmailAttachment("acc-1", "msg-1", "att-1");

        var result = Assert.IsInstanceOfType<ModelContextProtocol.Protocol.CallToolResult>(answer);
        var text = (ModelContextProtocol.Protocol.TextContentBlock)result.Content[0];
        var id = JsonDocument.Parse(text.Text).RootElement.GetProperty("attachmentId").GetString()!;
        var link = (ModelContextProtocol.Protocol.ResourceLinkBlock)result.Content[1];
        Assert.AreEqual("attachment://stash/" + id, link.Uri);
        Assert.AreEqual("scan.pdf", link.Name);
        Assert.AreEqual("application/pdf", link.MimeType);
        Assert.AreEqual(3L, link.Size);
        Assert.IsNotNull(store.TryRead(id));
    }

    [TestMethod]
    public void GetEmailAttachment_DescriptionsOfferALinkAndNoInlineBytes()
    {
        var method = typeof(GetEmailAttachmentTool).GetMethod(nameof(GetEmailAttachmentTool.GetEmailAttachment))!;
        var tool = ModelContextProtocol.Server.McpServerTool.Create(method, _ => null!);

        var parameters = method.GetParameters().Select(p => p.Name).ToList();
        CollectionAssert.DoesNotContain(parameters, "mode");
        StringAssert.Contains(tool.ProtocolTool.Description!, "attachment://");
        Assert.DoesNotContain("inline", tool.ProtocolTool.Description!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no MCP tool for downloading", tool.ProtocolTool.Description!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task StoreFull_SaysTheCapAndTheTtlAndOffersNoInlineMode()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");
        var (regExp, factExp, _) = WireProviderForFetch(
            account, "msg-1", "att-1",
            new EmailAttachmentContent { Name = "big.bin", ContentType = null, Bytes = new byte[8] });
        var options = new AttachmentStoreOptions { MaxBytesPerAttachment = 4 };
        var tenantContext = new TenantContext();
        using var tenantBinding = tenantContext.Bind(TestData.TenantA);
        var store = new InMemoryAttachmentStore(
            Options.Create(options), NullLogger<InMemoryAttachmentStore>.Instance, tenantContext);
        var tool = new GetEmailAttachmentTool(regExp.Instance(), factExp.Instance(), store,
            Options.Create(options), NullLogger<GetEmailAttachmentTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetEmailAttachment("acc-1", "msg-1", "att-1"));

        StringAssert.Contains(ex.Message, "Could not stash the attachment");
        StringAssert.Contains(ex.Message, $"{options.MaxBytesPerAttachment / (1024 * 1024)} MiB per-attachment limit");
        StringAssert.Contains(ex.Message, $"within {(int)options.Ttl.TotalMinutes} minutes");
        Assert.DoesNotContain("inline", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task ProviderReturnsNull_ThrowsMcpException()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");
        var (regExp, factExp, _) = WireProviderForFetch(account, "msg-1", "missing", null);

        var tool = new GetEmailAttachmentTool(regExp.Instance(), factExp.Instance(),
            new TestAttachmentStore(), Options.Create(new AttachmentStoreOptions()), NullLogger<GetEmailAttachmentTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetEmailAttachment("acc-1", "msg-1", "missing"));
        Assert.IsTrue(ex.Message.Contains("not found", StringComparison.Ordinal));
    }

    private static (
        IAccountRegistryCreateExpectations reg,
        IProviderServiceFactoryCreateExpectations fact,
        IProviderServiceCreateExpectations prov) WireProviderForFetch(
            AccountInfo account,
            string emailId,
            string attachmentId,
            EmailAttachmentContent? content)
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync(account.Id)
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetEmailAttachmentContentAsync(
            account.Id, emailId, attachmentId, Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(content));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider(account.Provider).ReturnValue(provExp.Instance());

        return (regExp, factExp, provExp);
    }
}
