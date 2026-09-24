using System.Text.Json.Nodes;
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
    private Task<string> GetEmailAttachmentAction(string? accountId, string? emailId, string? attachmentId) =>
        Impl<GetEmailAttachmentTool>().GetEmailAttachment(accountId!, emailId!, attachmentId!, "stash");

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
