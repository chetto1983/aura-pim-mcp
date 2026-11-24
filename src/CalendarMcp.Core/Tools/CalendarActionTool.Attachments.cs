using ModelContextProtocol.Protocol;

namespace CalendarMcp.Core.Tools;

public sealed partial class CalendarActionTool
{
    private Task<CallToolResult> GetEmailAttachmentAction(string? accountId, string? emailId, string? attachmentId) =>
        Impl<GetEmailAttachmentTool>().GetEmailAttachment(accountId!, emailId!, attachmentId!);

    private async Task<string> GetEmailAttachmentText(string? accountId, string? emailId, string? attachmentId) =>
        ((TextContentBlock)(await GetEmailAttachmentAction(accountId, emailId, attachmentId)).Content[0]).Text;

    internal static CallToolResult TextResult(string text) => new() { Content = [new TextContentBlock { Text = text }] };
}
