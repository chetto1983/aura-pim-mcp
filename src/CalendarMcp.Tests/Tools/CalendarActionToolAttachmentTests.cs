using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Core.Tools;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Rocks;

namespace CalendarMcp.Tests.Tools;

[TestClass]
public sealed class CalendarActionToolAttachmentTests
{
    [TestMethod]
    public async Task GetEmailAttachment_ReturnsALinkTheResourceServes()
    {
        var tenantContext = new TenantContext();
        var store = new InMemoryAttachmentStore(
            Options.Create(new AttachmentStoreOptions()), NullLogger<InMemoryAttachmentStore>.Instance, tenantContext);
        var registry = new IAccountRegistryCreateExpectations();
        registry.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(TestData.CreateAccount(id: "acc-1", provider: "microsoft365")));
        var provider = new IProviderServiceCreateExpectations();
        provider.Setups.GetEmailAttachmentContentAsync("acc-1", "email-1", "part-0", Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<EmailAttachmentContent?>(
                new EmailAttachmentContent { Name = "report.pdf", ContentType = null, Bytes = "%PDF-1.7"u8.ToArray() }));
        var factory = new IProviderServiceFactoryCreateExpectations();
        factory.Setups.GetProvider("microsoft365").ReturnValue(provider.Instance());

        await using var session = await InProcessMcpSession.StartAsync(tenantContext, store, TestData.TenantA, services =>
        {
            services.AddSingleton(registry.Instance());
            services.AddSingleton(factory.Instance());
        });
        var result = await session.Client.CallToolAsync("calendar", new Dictionary<string, object?>
        {
            ["action"] = "get_email_attachment",
            ["accountId"] = "acc-1",
            ["emailId"] = "email-1",
            ["attachmentId"] = "part-0",
        });

        Assert.AreNotEqual(true, result.IsError, string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        StringAssert.Contains(result.Content.OfType<TextContentBlock>().Single().Text, "\"attachmentId\"");
        var link = result.Content.OfType<ResourceLinkBlock>().Single();
        Assert.AreEqual("report.pdf", link.Name);
        Assert.AreEqual("application/pdf", link.MimeType);
        Assert.AreEqual(8L, link.Size);
        var read = await session.Client.ReadResourceAsync(link.Uri);
        CollectionAssert.AreEqual("%PDF-1.7"u8.ToArray(), ((BlobResourceContents)read.Contents.Single()).DecodedData.ToArray());
    }

    [TestMethod]
    public void WithAttachmentLink_KeepsTheStashJsonAsText()
    {
        const string stash = """{"attachmentId":"abc","name":"a.png","contentType":"image/png","size":3,"expiresAt":"2026-09-24T10:00:00Z"}""";

        var result = CalendarActionTool.WithAttachmentLink(stash);

        Assert.AreEqual(stash, ((TextContentBlock)result.Content[0]).Text);
        var link = (ResourceLinkBlock)result.Content[1];
        Assert.AreEqual("attachment://stash/abc", link.Uri);
        Assert.AreEqual("image/png", link.MimeType);
    }

    [TestMethod]
    [DataRow("""{"name":"a.png","size":3}""")]
    [DataRow("""{"attachmentId":"abc","size":3}""")]
    [DataRow("""{"attachmentId":"abc","name":"a.png"}""")]
    [DataRow("""{"attachmentId":42,"name":"a.png","size":3}""")]
    public void WithAttachmentLink_FailsLoudlyWhenUpstreamShapeChanges(string stash)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => CalendarActionTool.WithAttachmentLink(stash));
    }
}
