using CalendarMcp.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// get_email_attachment always stashes: <see cref="GetEmailAttachmentTool"/> builds the
/// <c>attachment://</c> link and the <c>attachmentId</c> that <c>send_email</c> takes. Upstream's
/// inline mode is not offered: base64 in the result is paid for by the model's context either way.
/// </summary>
public sealed partial class CalendarActionTool
{
    // The curated tool has no `mode` parameter, so the caller can never choose upstream's
    // inline mode -- rewrite that sentence into one the caller can act on, built from the
    // same AttachmentStoreOptions the store itself is configured from (one definition,
    // IAttachmentStore.cs), instead of pointing at a choice the caller does not have.
    private const string InlineModeHint = " Try inline mode if the file is small.";

    private async Task<CallToolResult> GetEmailAttachmentAction(string? accountId, string? emailId, string? attachmentId)
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

    private async Task<string> GetEmailAttachmentText(string? accountId, string? emailId, string? attachmentId) =>
        ((TextContentBlock)(await GetEmailAttachmentAction(accountId, emailId, attachmentId)).Content[0]).Text;

    internal static CallToolResult TextResult(string text) => new() { Content = [new TextContentBlock { Text = text }] };
}
