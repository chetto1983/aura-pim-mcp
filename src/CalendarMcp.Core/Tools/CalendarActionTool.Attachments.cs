using System.Text.Json.Nodes;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// get_email_attachment always stashes, and its result links the stash as an
/// <c>attachment://</c> resource (<see cref="EmailAttachmentResource"/>): a client that wants
/// the bytes reads them back on its own session, and one that does not pays nothing for them.
/// The <c>attachmentId</c> in the text stays what <c>send_email</c> takes. Upstream's inline
/// mode is not offered: base64 in the result is paid for by the model's context either way.
/// </summary>
public sealed partial class CalendarActionTool
{
    // The curated tool has no `mode` parameter, so the caller can never choose upstream's
    // inline mode -- rewrite that sentence into one the caller can act on, built from the
    // same AttachmentStoreOptions the store itself is configured from (one definition,
    // IAttachmentStore.cs), instead of pointing at a choice the caller does not have.
    private const string InlineModeHint = " Try inline mode if the file is small.";

    private async Task<string> GetEmailAttachmentAction(string? accountId, string? emailId, string? attachmentId)
    {
        try
        {
            return await Impl<GetEmailAttachmentTool>().GetEmailAttachment(accountId!, emailId!, attachmentId!, "stash");
        }
        catch (McpException ex) when (ex.Message.Contains(InlineModeHint, StringComparison.Ordinal))
        {
            var options = _services.GetRequiredService<IOptions<AttachmentStoreOptions>>().Value;
            var capMiB = options.MaxBytesPerAttachment / (1024 * 1024);
            var ttlMinutes = (int)options.Ttl.TotalMinutes;
            var actionable =
                $" A file larger than the {capMiB} MiB per-attachment limit cannot be fetched; " +
                $"otherwise the server's attachment store is full until earlier attachments expire " +
                $"(within {ttlMinutes} minutes), so retry then.";
            throw new McpException(ex.Message.Replace(InlineModeHint, actionable, StringComparison.Ordinal), ex);
        }
    }

    /// <summary>
    /// The stash JSON as text plus a resource link to it. Fails loudly if upstream renames a
    /// field the link is built from: a link without its id would point at nothing.
    /// </summary>
    internal static CallToolResult WithAttachmentLink(string stashJson)
    {
        var stash = ParseObject(stashJson);
        var id = RequireString(stash, "attachmentId", "get_email_attachment");
        var name = RequireString(stash, "name", "get_email_attachment");
        var size = stash["size"] is JsonValue sizeValue && sizeValue.TryGetValue<long>(out var bytes)
            ? bytes
            : throw new InvalidOperationException("get_email_attachment returned no 'size' number.");
        var contentType = stash["contentType"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var type)
            ? type
            : null;
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = stashJson },
                new ResourceLinkBlock
                {
                    Uri = EmailAttachmentResource.UriFor(id),
                    Name = name,
                    MimeType = EmailAttachmentResource.MimeTypeFor(name, contentType),
                    Size = size,
                },
            ],
        };
    }

    internal static CallToolResult TextResult(string text) => new() { Content = [new TextContentBlock { Text = text }] };
}
