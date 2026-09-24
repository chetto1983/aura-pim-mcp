using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.Core.Tools;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CalendarMcp.Tests.Tools;

[TestClass]
public sealed class EmailAttachmentResourceTests
{
    [TestMethod]
    public async Task ResourcesRead_ReturnsTheStashedBytesToTheTenantThatStashedThem()
    {
        var tenantContext = new TenantContext();
        var store = NewStore(tenantContext);
        var stored = Stash(store, tenantContext, TestData.TenantA, "invoice.pdf", null, "%PDF-1.7"u8.ToArray());

        await using var session = await InProcessMcpSession.StartAsync(tenantContext, store, TestData.TenantA);
        var result = await session.Client.ReadResourceAsync(EmailAttachmentResource.UriFor(stored.Id));

        var blob = (BlobResourceContents)result.Contents.Single();
        Assert.AreEqual("application/pdf", blob.MimeType);
        CollectionAssert.AreEqual("%PDF-1.7"u8.ToArray(), blob.DecodedData.ToArray());
    }

    [TestMethod]
    public async Task ResourcesRead_RefusesAnotherTenantsAttachment()
    {
        var tenantContext = new TenantContext();
        var store = NewStore(tenantContext);
        var stored = Stash(store, tenantContext, TestData.TenantA, "invoice.pdf", "application/pdf", [1, 2, 3]);

        await using var session = await InProcessMcpSession.StartAsync(tenantContext, store, TestData.TenantB);
        var error = await Assert.ThrowsAsync<McpException>(
            () => session.Client.ReadResourceAsync(EmailAttachmentResource.UriFor(stored.Id)).AsTask());

        StringAssert.Contains(error.Message, "attachment expired or unknown; call get_email_attachment again");
    }

    [TestMethod]
    public async Task ResourcesRead_RefusesAnExpiredAttachment()
    {
        var tenantContext = new TenantContext();
        var store = NewStore(tenantContext, new AttachmentStoreOptions { Ttl = TimeSpan.Zero });
        var stored = Stash(store, tenantContext, TestData.TenantA, "invoice.pdf", "application/pdf", [1, 2, 3]);

        await using var session = await InProcessMcpSession.StartAsync(tenantContext, store, TestData.TenantA);
        var error = await Assert.ThrowsAsync<McpException>(
            () => session.Client.ReadResourceAsync(EmailAttachmentResource.UriFor(stored.Id)).AsTask());

        StringAssert.Contains(error.Message, "expired or unknown");
    }

    [TestMethod]
    public async Task ResourcesRead_DoesNotConsumeTheStash()
    {
        var tenantContext = new TenantContext();
        var store = NewStore(tenantContext);
        var stored = Stash(store, tenantContext, TestData.TenantA, "invoice.pdf", "application/pdf", [1, 2, 3]);

        await using (var session = await InProcessMcpSession.StartAsync(tenantContext, store, TestData.TenantA))
            await session.Client.ReadResourceAsync(EmailAttachmentResource.UriFor(stored.Id));

        using (tenantContext.Bind(TestData.TenantA))
            Assert.IsNotNull(store.TryConsume(stored.Id), "send_email must still find the stash after a read.");
    }

    [TestMethod]
    public async Task ResourcesRead_AcceptsALinkParsedAsSystemUri()
    {
        var tenantContext = new TenantContext();
        var store = NewStore(tenantContext);
        byte[] bytes = [1, 2, 3];

        // The store mints a 22-char base64url id; an all-lowercase draw is astronomically
        // unlikely (~5e-5), but the bound keeps this test from ever hanging on bad luck.
        StoredAttachment? stored = null;
        for (var attempt = 0; attempt < 50 && stored?.Id.Any(char.IsUpper) != true; attempt++)
            stored = Stash(store, tenantContext, TestData.TenantA, "invoice.pdf", "application/pdf", bytes);
        Assert.IsTrue(stored?.Id.Any(char.IsUpper) == true, "expected a mixed-case id within 50 stashes.");

        await using var session = await InProcessMcpSession.StartAsync(tenantContext, store, TestData.TenantA);
        var result = await session.Client.ReadResourceAsync(new Uri(EmailAttachmentResource.UriFor(stored!.Id)));

        var blob = (BlobResourceContents)result.Contents.Single();
        CollectionAssert.AreEqual(bytes, blob.DecodedData.ToArray());
    }

    [TestMethod]
    [DataRow("report.pdf", null, "application/pdf")]
    [DataRow("report.pdf", "application/octet-stream", "application/pdf")]
    [DataRow("report.pdf", "", "application/pdf")]
    [DataRow("photo.bin", "image/png", "image/png")]
    [DataRow("no-extension", null, "application/octet-stream")]
    public void MimeTypeFor_PrefersASpecificContentTypeThenTheName(string name, string? contentType, string expected)
    {
        Assert.AreEqual(expected, EmailAttachmentResource.MimeTypeFor(name, contentType));
    }

    private static InMemoryAttachmentStore NewStore(ITenantContext tenantContext, AttachmentStoreOptions? options = null) =>
        new(Options.Create(options ?? new AttachmentStoreOptions()), NullLogger<InMemoryAttachmentStore>.Instance, tenantContext);

    private static StoredAttachment Stash(
        IAttachmentStore store, ITenantContext tenantContext, string tenant, string name, string? contentType, byte[] bytes)
    {
        using (tenantContext.Bind(tenant))
            return store.Put(name, contentType, bytes)!;
    }
}
