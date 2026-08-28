using CalendarMcp.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// The twelve actions the first curation round left behind, exposed by FORWARDING to the
/// implementation classes that already carry them.
/// </summary>
/// <remarks>
/// The first round (D-21/46-05) folded fourteen tools into the curated surface by moving each
/// body into an <c>*Action</c> method and deleting the class it came from. Twelve classes were
/// never folded, and since <c>Program.cs</c> registers only <c>WithCalendarActionTool()</c>,
/// they were left implemented, tested, and unreachable -- no MCP client could call them.
/// Upstream registers 29 tools; the curated surface published 17. These twelve are the
/// difference, so the fork now offers what the original does behind a single tool.
/// <para>
/// The gap read as a hole rather than a shortfall: <c>create_event</c> and
/// <c>update_event</c> were curated while <c>delete_event</c> was not, so an agent could put an
/// event on a calendar and never take it off (measured live -- a test event had to be removed by
/// hand). The server's own instructions, meanwhile, told callers to invoke <c>get_guide</c>, a
/// tool the curated enum did not contain.
/// </para>
/// <para>
/// These forward instead of re-implementing. Copying twelve tested method bodies into this
/// facade would duplicate the logic and leave two copies to drift; the implementation classes
/// stay the single definition of what each action does, and this file stays a routing table.
/// Construction goes through <see cref="ActivatorUtilities"/> rather than explicit
/// <c>new</c> calls because their constructors are not uniform -- <c>UnsubscribeFromEmailTool</c>
/// also needs an <c>UnsubscribeExecutor</c>, <c>GetGuideTool</c> needs only a logger -- and
/// hand-wiring them made this file wrong twice before the compiler caught it. Letting the
/// container answer means a dependency added upstream needs no edit here.
/// </para>
/// <para>
/// Argument validation is deliberately NOT repeated. Every implementation already calls
/// <c>ToolGuard.RequireNonEmpty</c> on what it requires and throws <c>McpException</c> naming the
/// missing parameter, so a second check here would only be a second place to go stale.
/// </para>
/// </remarks>
public sealed partial class CalendarActionTool
{
    private T Impl<T>() where T : notnull => ActivatorUtilities.CreateInstance<T>(_services);

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
