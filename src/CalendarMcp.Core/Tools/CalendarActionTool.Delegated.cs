using CalendarMcp.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// Every action except the two EventRef adapters (CalendarActionTool.Calendar.cs), exposed by
/// FORWARDING to the upstream implementation class that already carries it.
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
/// Argument validation is NOT repeated, with one exception: a parameter upstream declares
/// non-nullable is required by its own tool schema, while every parameter of the multiplexed
/// schema is optional. Those few are checked here before they are unwrapped; everything else is
/// the implementation's to validate.
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

    private Task<string> MarkEmailReadAction(string? accountId, string? emailId, bool? isRead)
    {
        ToolGuard.RequireNonEmpty(accountId, nameof(accountId));
        ToolGuard.RequireNonEmpty(emailId, nameof(emailId));
        if (isRead is null)
            throw new McpException("mark_email_read requires 'isRead' (true to mark read, false to mark unread).");
        return Impl<MarkEmailAsReadTool>().MarkEmailAsRead(accountId!, emailId!, isRead.Value);
    }

    private Task<string> MoveEmailAction(string? accountId, string? emailId, string? destination) =>
        Impl<MoveEmailTool>().MoveEmail(accountId!, emailId!, destination!);

    private Task<string> ListCalendarsAction(string? accountId) =>
        Impl<ListCalendarsTool>().ListCalendars(accountId);

    private Task<string> CreateEventAction(
        string? subject, DateTime? start, DateTime? end, string? accountId, string? calendarId,
        string? location, List<string>? attendees, string? body, string? timeZone, bool? isAllDay)
    {
        if (string.IsNullOrEmpty(subject))
            throw new McpException("subject is required.");
        if (start is null)
            throw new McpException("start is required.");
        if (end is null)
            throw new McpException("end is required.");
        return Impl<CreateEventTool>().CreateEvent(
            subject, start.Value, end.Value, accountId, calendarId, location, attendees, body, timeZone, isAllDay ?? false);
    }

    private Task<string> UpdateEventAction(
        string? accountId, string? calendarId, string? eventId, string? subject, DateTime? start, DateTime? end,
        string? location, List<string>? attendees, string? timeZone, bool? isAllDay) =>
        Impl<UpdateEventTool>().UpdateEvent(
            accountId!, calendarId!, eventId!, subject, start, end, location, attendees, timeZone, isAllDay);

    private Task<string> RespondToEventAction(
        string? eventId, string? response, string? accountId, string? calendarId, string? comment) =>
        Impl<RespondToEventTool>().RespondToEvent(eventId!, response!, accountId, calendarId, comment);

    private Task<string> GetContactsAction(string? accountId, int? count) =>
        Impl<GetContactsTool>().GetContacts(accountId, count ?? 50);

    private Task<string> SearchContactsAction(string? query, string? accountId, int? count) =>
        Impl<SearchContactsTool>().SearchContacts(query!, accountId, count ?? 50);

    private Task<string> GetContactDetailsAction(string? accountId, string? contactId) =>
        Impl<GetContactDetailsTool>().GetContactDetails(accountId!, contactId!);

    private Task<string> DeleteEventAction(string? eventId, string? accountId, string? calendarId) =>
        Impl<DeleteEventTool>().DeleteEvent(eventId!, accountId, calendarId);

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
        Impl<BulkDeleteEmailsTool>().BulkDeleteEmails(items ?? []);

    private Task<string> BulkMarkEmailsReadAction(BulkEmailItem[]? items, bool? isRead) =>
        Impl<BulkMarkEmailsAsReadTool>().BulkMarkEmailsAsRead(items ?? [], isRead ?? true);

    private Task<string> BulkMoveEmailsAction(BulkEmailItem[]? items, string? destination) =>
        Impl<BulkMoveEmailsTool>().BulkMoveEmails(items ?? [], destination!);
}
