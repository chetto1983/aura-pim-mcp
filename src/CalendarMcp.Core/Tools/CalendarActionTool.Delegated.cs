using CalendarMcp.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// Every action except the calendar-event ones (CalendarActionTool.Calendar.cs, which address
/// events by EventRef), exposed by FORWARDING to the upstream implementation class that already
/// carries it.
/// </summary>
/// <remarks>
/// The first curation round (D-21/46-05) moved fourteen tool bodies into this facade and deleted
/// the classes they came from. Those copies stopped receiving upstream fixes: the 1.8.3 sync found
/// permissions, provider-error reporting, all-day events and folder reads applied to the classes
/// and to none of the copies, and every one of them surfaced as a modify/delete conflict. The
/// implementation classes are therefore the single definition of what each action does, and this
/// file stays a routing table.
/// <para>
/// Construction goes through <see cref="ActivatorUtilities"/> because the constructors are not
/// uniform -- <c>UnsubscribeFromEmailTool</c> also needs an <c>UnsubscribeExecutor</c>,
/// <c>GetGuideTool</c> only a logger -- so a dependency added upstream needs no edit here.
/// </para>
/// <para>
/// Argument validation is NOT repeated, with one exception: a value-type parameter upstream
/// declares non-nullable (<c>isRead</c>, an event's <c>start</c>/<c>end</c>) is required by its
/// own tool schema, while every parameter of the multiplexed schema is optional. Unwrapping it
/// would invent a value, so it is checked here; required strings, lists and arrays are passed
/// through as-is because the implementation already rejects them when missing.
/// </para>
/// </remarks>
public sealed partial class CalendarActionTool
{
    private T Impl<T>() where T : notnull => ActivatorUtilities.CreateInstance<T>(_services);

    private Task<string> ListAccountsAction() =>
        Impl<ListAccountsTool>().ListAccounts();

    private Task<string> GetEmailsAction(string? accountId, int? count, bool? unreadOnly, string? folder) =>
        Impl<GetEmailsTool>().GetEmails(accountId, count ?? 20, unreadOnly ?? false, folder);

    private Task<string> GetEmailDetailsAction(string? accountId, string? emailId) =>
        Impl<GetEmailDetailsTool>().GetEmailDetails(accountId!, emailId!);

    private Task<string> SearchEmailsAction(
        string? query, string? accountId, int? count, DateTime? fromDate, DateTime? toDate, string? folder) =>
        Impl<SearchEmailsTool>().SearchEmails(query!, accountId, count ?? 20, fromDate, toDate, folder);

    private Task<string> SendEmailAction(
        List<string>? to, string? subject, string? body, string? accountId, string? bodyFormat,
        List<string>? cc, List<OutboundEmailAttachment>? attachments, string? textBody, string? htmlBody)
    {
        if (string.IsNullOrEmpty(subject))
            throw new McpException("subject is required.");
        return Impl<SendEmailTool>().SendEmail(
            to!, subject, body ?? "", accountId, bodyFormat ?? "html", cc, attachments, textBody, htmlBody);
    }

    private Task<string> DeleteEmailAction(string? accountId, string? emailId) =>
        Impl<DeleteEmailTool>().DeleteEmail(accountId!, emailId!);

    private Task<string> MarkEmailReadAction(string? accountId, string? emailId, bool? isRead) =>
        Impl<MarkEmailAsReadTool>().MarkEmailAsRead(accountId!, emailId!, RequireIsRead(isRead, "mark_email_read"));

    private Task<string> MoveEmailAction(string? accountId, string? emailId, string? destination) =>
        Impl<MoveEmailTool>().MoveEmail(accountId!, emailId!, destination!);

    private Task<string> ListCalendarsAction(string? accountId) =>
        Impl<ListCalendarsTool>().ListCalendars(accountId);

    private Task<string> GetContactsAction(string? accountId, int? count) =>
        Impl<GetContactsTool>().GetContacts(accountId, count ?? 50);

    private Task<string> SearchContactsAction(string? query, string? accountId, int? count) =>
        Impl<SearchContactsTool>().SearchContacts(query!, accountId, count ?? 50);

    private Task<string> GetContactDetailsAction(string? accountId, string? contactId) =>
        Impl<GetContactDetailsTool>().GetContactDetails(accountId!, contactId!);

    private Task<string> CreateContactAction(
        string? displayName, string? accountId, string? givenName, string? surname,
        string? email, string? phone, string? jobTitle, string? companyName, string? notes)
    {
        ToolGuard.RequireNonEmpty(displayName, nameof(displayName));
        return Impl<CreateContactTool>().CreateContact(
            displayName!, accountId, givenName, surname, email, phone, jobTitle, companyName, notes);
    }

    private Task<string> UpdateContactAction(
        string? accountId, string? contactId, string? displayName, string? givenName, string? surname,
        string? email, string? phone, string? jobTitle, string? companyName, string? notes) =>
        Impl<UpdateContactTool>().UpdateContact(
            accountId!, contactId!, displayName, givenName, surname, email, phone, jobTitle, companyName, notes);

    private Task<string> DeleteContactAction(string? accountId, string? contactId) =>
        Impl<DeleteContactTool>().DeleteContact(accountId!, contactId!);

    private Task<string> GetEmailAttachmentAction(
        string? accountId, string? emailId, string? attachmentId, string? mode) =>
        Impl<GetEmailAttachmentTool>().GetEmailAttachment(accountId!, emailId!, attachmentId!, mode ?? "stash");

    private Task<string> GetContextualEmailSummaryAction(
        string? topics, int? countPerAccount, bool? unreadOnly, bool? includeBodyPreview, int? maxSamplesPerCluster) =>
        Impl<GetContextualEmailSummaryTool>().GetContextualEmailSummary(
            topics, countPerAccount ?? 50, unreadOnly ?? false, includeBodyPreview ?? false, maxSamplesPerCluster ?? 5);

    // GetGuide reads bundled markdown and is synchronous; the dispatch table is uniformly
    // Task<string>, so the already-computed value is wrapped rather than the guide made async.
    private Task<string> GetGuideAction(string? name) =>
        Task.FromResult(Impl<GetGuideTool>().GetGuide(name));

    private Task<string> GetUnsubscribeInfoAction(string? accountId, string? emailId) =>
        Impl<GetUnsubscribeInfoTool>().GetUnsubscribeInfo(accountId!, emailId!);

    private Task<string> UnsubscribeFromEmailAction(string? accountId, string? emailId, string? method) =>
        Impl<UnsubscribeFromEmailTool>().UnsubscribeFromEmail(accountId!, emailId!, method ?? "auto");

    private Task<string> BulkDeleteEmailsAction(BulkEmailItem[]? items) =>
        Impl<BulkDeleteEmailsTool>().BulkDeleteEmails(items!);

    private Task<string> BulkMarkEmailsReadAction(BulkEmailItem[]? items, bool? isRead) =>
        Impl<BulkMarkEmailsAsReadTool>().BulkMarkEmailsAsRead(items!, RequireIsRead(isRead, "bulk_mark_emails_read"));

    private Task<string> BulkMoveEmailsAction(BulkEmailItem[]? items, string? destination) =>
        Impl<BulkMoveEmailsTool>().BulkMoveEmails(items!, destination!);

    private static bool RequireIsRead(bool? isRead, string action) =>
        isRead ?? throw new McpException($"{action} requires 'isRead' (true to mark read, false to mark unread).");
}
