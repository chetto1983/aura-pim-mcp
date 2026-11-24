using System.ComponentModel;
using System.Text.Json;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// Fetches the bytes of one inbound email attachment. The server downloads from the upstream
/// provider and drops the bytes into the same store used by the upload endpoint; the result
/// links the stash as an <c>attachment://</c> resource (<see cref="EmailAttachmentResource"/>)
/// and carries the attachmentId the agent can pass to <c>send_email</c>. A client that wants
/// the bytes reads them back on its own session, and one that does not pays nothing for them.
/// There is no inline mode: base64 in the result is paid for by the model's context either way.
/// </summary>
[McpServerToolType]
public sealed class GetEmailAttachmentTool(
    IAccountRegistry accountRegistry,
    IProviderServiceFactory providerFactory,
    IAttachmentStore attachmentStore,
    IOptions<AttachmentStoreOptions> storeOptions,
    ILogger<GetEmailAttachmentTool> logger)
{
    [McpServerTool, Description(
        "Fetch one attachment of a received email into the server's attachment store. Required: accountId, emailId, attachmentId — get them from get_email_details. The result is an attachment:// resource link (the client reads the bytes through that link; they never enter your context) plus an attachmentId you pass to send_email to forward the file. Stash IDs are single-use for send_email and expire after a while.")]
    public async Task<CallToolResult> GetEmailAttachment(
        [Description("Required. Account that owns the email.")] string accountId,
        [Description("Required. Email ID — pass as parameter name 'emailId' (NOT 'messageId'). Use the value from the 'id' field on get_email_details / get_emails / search_emails.")] string emailId,
        [Description("Required. Provider-side attachment ID, from the attachments[] array on get_email_details (e.g. 'part-0' for Gmail/IMAP, an opaque string for Microsoft Graph).")] string attachmentId)
    {
        ToolGuard.RequireNonEmpty(accountId, nameof(accountId));
        ToolGuard.RequireNonEmpty(emailId, nameof(emailId));
        ToolGuard.RequireNonEmpty(attachmentId, nameof(attachmentId));

        var account = await ToolGuard.RequireAccountAsync(
            accountRegistry, accountId, AccountPermission.EmailRead);

        try
        {
            var provider = providerFactory.GetProvider(account.Provider);
            var content = await provider.GetEmailAttachmentContentAsync(
                accountId, emailId, attachmentId, CancellationToken.None);

            if (content == null)
                throw new McpException($"Attachment '{attachmentId}' on email '{emailId}' was not found or could not be fetched.");

            var stored = attachmentStore.Put(content.Name, content.ContentType, content.Bytes);
            if (stored == null)
                throw new McpException(StashRefusal(content.Bytes.LongLength));

            logger.LogInformation(
                "Stashed inbound attachment {AttachmentId} from {EmailId} as {StoreId} ({Size} bytes)",
                attachmentId, emailId, stored.Id, stored.Bytes.Length);

            var stash = JsonSerializer.Serialize(new
            {
                attachmentId = stored.Id,
                name = stored.Name,
                contentType = stored.ContentType,
                size = stored.Bytes.LongLength,
                expiresAt = stored.ExpiresAt,
            });
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = stash },
                    new ResourceLinkBlock
                    {
                        Uri = EmailAttachmentResource.UriFor(stored.Id),
                        Name = stored.Name,
                        MimeType = EmailAttachmentResource.MimeTypeFor(stored.Name, stored.ContentType),
                        Size = stored.Bytes.LongLength,
                    },
                ],
            };
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error in get_email_attachment tool");
            throw ToolGuard.Failure("fetch attachment", ex);
        }
    }

    private string StashRefusal(long size)
    {
        var options = storeOptions.Value;
        return $"Could not stash the attachment ({size:N0} bytes). " +
            $"A file larger than the {options.MaxBytesPerAttachment / (1024 * 1024)} MiB per-attachment limit cannot be fetched; " +
            $"otherwise the server's attachment store is full until earlier attachments expire " +
            $"(within {(int)options.Ttl.TotalMinutes} minutes), so retry then.";
    }
}
