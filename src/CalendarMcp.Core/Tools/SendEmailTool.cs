using System.ComponentModel;
using System.Text.Json;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// MCP tool for sending emails
/// </summary>
[McpServerToolType]
public sealed class SendEmailTool(
    IAccountRegistry accountRegistry,
    IProviderServiceFactory providerFactory,
    IAttachmentStore attachmentStore,
    ILogger<SendEmailTool> logger)
{
    // Total decoded attachment payload limit per message. Keeps the JSON tool
    // call within agent limits and matches typical provider caps (~25 MB).
    private const long MaxTotalAttachmentBytes = 25L * 1024 * 1024;

    [McpServerTool(Name = "send_email"), Description("Send an email, optionally with file attachments. Required: accountId, the account to send from (call list_accounts). When the user has more than one account and has not named one, ask which.")]
    public async Task<string> SendEmail(
        [Description("Required. Account ID to send from, from list_accounts. The mail goes out from exactly this account.")] string accountId,
        [Description("Recipient email address(es). Supply as a JSON array of strings, e.g. [\"alice@example.com\"] or [\"alice@example.com\",\"bob@example.com\"].")] List<string> to,
        [Description("Email subject line")] string subject,
        [Description("Email body content. Use HTML when bodyFormat is 'html' (the default). Ignored when bodyFormat is 'multipart'.")] string body = "",
        [Description("Body content format: 'html' (default), 'text', or 'multipart'. When 'multipart' is specified, provide textBody and htmlBody instead of body. Note: bodyFormat 'html' requires actual HTML markup; Markdown/plain text is not automatically converted to HTML.")] string bodyFormat = "html",
        [Description("CC recipient email addresses")] List<string>? cc = null,
        [Description("Optional file attachments as a JSON array. Each item must set EITHER \"attachmentId\" (preferred for anything non-trivial: upload the file via POST /attachments first to get the ID) OR \"base64Content\" (the raw bytes encoded as base64; use only for very small files). \"name\" is required when using base64Content and optional when using attachmentId. \"contentType\" is optional. Total decoded payload per message must stay under 25 MB; Microsoft 365 and Outlook.com cap each individual attachment at 3 MB. Examples: [{\"attachmentId\":\"AbCd...\"}] or [{\"name\":\"x.pdf\",\"base64Content\":\"...\"}].")] List<OutboundEmailAttachment>? attachments = null,
        [Description("Plain-text body for multipart/alternative messages. Required when bodyFormat is 'multipart'.")] string? textBody = null,
        [Description("HTML body for multipart/alternative messages. Required when bodyFormat is 'multipart'. Must contain actual HTML markup.")] string? htmlBody = null)
    {
        ToolGuard.RequireAccountId(accountId, "send_email", "send from");
        ToolGuard.RequireNonEmpty(subject, nameof(subject));
        // A JSON null binds as null over the declared default.
        body ??= "";
        bodyFormat ??= "html";

        // Strip CDATA wrappers if present (LLMs sometimes wrap content in XML CDATA)
        body = StripCdataWrapper(body);
        if (textBody != null) textBody = StripCdataWrapper(textBody);
        if (htmlBody != null) htmlBody = StripCdataWrapper(htmlBody);

        // Validate multipart parameters
        if (bodyFormat.Equals("multipart", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(textBody) || string.IsNullOrEmpty(htmlBody))
                throw new McpException("Both 'textBody' and 'htmlBody' are required when bodyFormat is 'multipart'.");
        }

        if (to is null || to.Count == 0)
            throw new McpException("At least one recipient address is required in the 'to' field.");

        List<OutboundEmailAttachment>? resolvedAttachments = null;
        if (attachments is { Count: > 0 })
        {
            var shapeError = ValidateAttachmentShapes(attachments);
            if (shapeError != null)
                throw new McpException(shapeError);

            // Second pass: consume any AttachmentId-backed entries. From this
            // point on, IDs are removed from the store; an error after this
            // requires the agent to re-upload.
            var (resolved, consumeError) = ResolveAttachments(attachments);
            if (consumeError != null)
                throw new McpException(consumeError);
            resolvedAttachments = resolved;
        }

        var toJoined = string.Join(", ", to);

        logger.LogInformation("Sending email: to={To}, subject={Subject}, accountId={AccountId}",
            toJoined, subject, accountId);

        var account = await ToolGuard.RequireAccountAsync(
            accountRegistry, accountId, Models.AccountPermission.EmailSend);

        try
        {
            // Send email
            var provider = providerFactory.GetProvider(account.Provider);
            var messageId = await provider.SendEmailAsync(
                account.Id, toJoined, subject, body, bodyFormat, cc, resolvedAttachments,
                textBody, htmlBody, CancellationToken.None);

            var result = new
            {
                success = true,
                messageId = messageId,
                accountUsed = account.Id
            };

            logger.LogInformation("Sent email from account {AccountId} to {To}", account.Id, toJoined);

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error in send_email tool");
            throw ToolGuard.Failure("send email", ex);
        }
    }

    /// <summary>
    /// First pass: structural validation only. Does NOT touch the attachment
    /// store. Returns an error string for the caller, or null if all items are
    /// well-formed.
    /// </summary>
    private static string? ValidateAttachmentShapes(List<OutboundEmailAttachment> attachments)
    {
        long inlineEstimatedBytes = 0;
        for (var i = 0; i < attachments.Count; i++)
        {
            var att = attachments[i];
            var hasInline = !string.IsNullOrEmpty(att.Base64Content);
            var hasId = !string.IsNullOrEmpty(att.AttachmentId);

            if (hasInline && hasId)
            {
                return $"attachments[{i}]: set either 'base64Content' or 'attachmentId', not both.";
            }
            if (!hasInline && !hasId)
            {
                return $"attachments[{i}]: set either 'base64Content' (inline bytes) or 'attachmentId' (from POST /attachments).";
            }
            if (hasInline && string.IsNullOrWhiteSpace(att.Name))
            {
                return $"attachments[{i}].name is required when using base64Content.";
            }
            if (hasInline)
            {
                // Cheap size estimate from base64 length; overestimates by up
                // to 2 bytes per attachment, which is fine for the 25 MB cap.
                var len = att.Base64Content!.Length;
                inlineEstimatedBytes += (long)(len / 4) * 3;
                if (inlineEstimatedBytes > MaxTotalAttachmentBytes)
                {
                    return $"Total inline attachment size exceeds {MaxTotalAttachmentBytes:N0} bytes.";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Second pass: resolves any <see cref="OutboundEmailAttachment.AttachmentId"/>
    /// entries to inline bytes by consuming them from the store. Single-use:
    /// successfully consumed IDs are removed, so an error after this point
    /// requires the agent to re-upload.
    /// </summary>
    private (List<OutboundEmailAttachment>? resolved, string? error) ResolveAttachments(
        List<OutboundEmailAttachment> attachments)
    {
        var result = new List<OutboundEmailAttachment>(attachments.Count);
        long totalBytes = 0;

        for (var i = 0; i < attachments.Count; i++)
        {
            var att = attachments[i];

            if (!string.IsNullOrEmpty(att.AttachmentId))
            {
                var stored = attachmentStore.TryConsume(att.AttachmentId);
                if (stored == null)
                {
                    return (null, $"attachments[{i}]: attachmentId '{att.AttachmentId}' is unknown or expired. Re-upload via POST /attachments.");
                }
                totalBytes += stored.Bytes.Length;
                if (totalBytes > MaxTotalAttachmentBytes)
                {
                    return (null, $"Total attachment size exceeds {MaxTotalAttachmentBytes:N0} bytes.");
                }
                result.Add(new OutboundEmailAttachment
                {
                    Name = string.IsNullOrWhiteSpace(att.Name) ? stored.Name : att.Name,
                    ContentType = att.ContentType ?? stored.ContentType,
                    Base64Content = Convert.ToBase64String(stored.Bytes),
                });
            }
            else
            {
                // Inline base64 — already validated by ValidateAttachmentShapes.
                result.Add(att);
            }
        }

        return (result, null);
    }

    /// <summary>
    /// Strips CDATA wrappers from content if present.
    /// LLMs sometimes wrap HTML content in XML CDATA sections which are not valid HTML.
    /// </summary>
    private static string StripCdataWrapper(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var trimmed = content.Trim();
        
        // Check for CDATA wrapper: <![CDATA[...]]>
        if (trimmed.StartsWith("<![CDATA[", StringComparison.OrdinalIgnoreCase) &&
            trimmed.EndsWith("]]>", StringComparison.Ordinal))
        {
            return trimmed[9..^3]; // Remove "<![CDATA[" (9 chars) and "]]>" (3 chars)
        }

        return content;
    }
}
