using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarMcp.Core.Apps;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// Curated, action-multiplexed MCP tool replacing the 29 individually
/// registered calendar/mail/contacts tools upstream advertises.
/// One model-facing tool, one <c>action</c> discriminator; each action
/// forwards to the upstream implementation class that defines it
/// (CalendarActionTool.Delegated.cs) -- only the registration/dispatch
/// layer collapses.
/// </summary>
/// <remarks>
/// The event actions take no <c>accountId</c>: they resolve the account from
/// the opaque <c>eventId</c> reference that <c>get_calendar_events</c> and
/// <c>create_event</c> return (see <see cref="EventRef"/> and
/// CalendarActionTool.Calendar.cs).
/// </remarks>
public sealed partial class CalendarActionTool
{
    private readonly ITenantContext _tenantContext;

    // The implementation classes the actions forward to have non-uniform constructors:
    // UnsubscribeFromEmailTool wants an UnsubscribeExecutor, GetGuideTool only a logger,
    // and hand-wiring each one made this file wrong twice before the compiler caught it.
    // Holding the provider lets ActivatorUtilities resolve whatever each class asks for,
    // so a dependency added upstream needs no edit here.
    private readonly IServiceProvider _services;

    public CalendarActionTool(IServiceProvider services, ITenantContext tenantContext)
    {
        _services = services;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// The 29 curated action names, in the exact casing the design doc's
    /// calendar action table specifies. Single source of truth for both the
    /// published JSON schema <c>enum</c> (see <see cref="SchemaOptions"/>)
    /// and this method's own unknown-action validation -- there is
    /// deliberately no second list to drift out of sync with the first.
    /// </summary>
    internal static readonly IReadOnlyList<string> ActionNames =
    [
        "list_accounts",
        "get_emails",
        "get_email_details",
        "search_emails",
        "list_calendars",
        "get_calendar_events",
        "get_calendar_event_details",
        "get_contacts",
        "search_contacts",
        "get_contact_details",
        "create_event",
        "update_event",
        "respond_to_event",
        "send_email",
        "delete_email",
        "mark_email_read",
        "move_email",
        "delete_event",
        "create_contact",
        "update_contact",
        "delete_contact",
        "get_email_attachment",
        "get_contextual_email_summary",
        "get_guide",
        "get_unsubscribe_info",
        "unsubscribe_from_email",
        "bulk_delete_emails",
        "bulk_mark_emails_read",
        "bulk_move_emails",
    ];

    private const string ToolDescription = """
        Unified access to email, calendar, and contacts across Microsoft 365, Google Workspace/Gmail, Outlook.com, and IMAP/SMTP mailboxes. Exactly one action per call, selected via the required `action` argument:
        - list_accounts: list configured accounts. No arguments.
        - get_emails: recent emails, newest first. accountId, count, unreadOnly, folder optional.
        - get_email_details: full email body and attachments. Requires accountId, emailId.
        - search_emails: full-text email search. Requires query. accountId, count, fromDate, toDate, folder optional.
        - send_email: send an email, optional attachments. Requires to, subject. accountId, body, bodyFormat, cc, attachments, textBody, htmlBody optional.
        - list_calendars: list calendars. accountId optional.
        - get_calendar_events: events on a range of local days in timeZone. Requires timeZone. accountId, calendarId, startDate, endDate, count optional. Each event's eventId is an opaque reference that names its account; the event actions below take it unchanged and no accountId.
        - get_calendar_event_details: full event detail. Requires timeZone, calendarId, eventId.
        - create_event: create an event; returns its eventId reference. Requires subject, start, end. accountId, calendarId, location, attendees, body, timeZone, isAllDay optional.
        - update_event: update an event. Requires calendarId, eventId. subject, start, end, location, attendees, timeZone, isAllDay optional.
        - respond_to_event: accept, tentative, or decline an invite. Requires eventId, response. calendarId, comment optional.
        - get_contacts: list contacts. accountId, count optional.
        - search_contacts: search contacts. Requires query. accountId, count optional.
        - get_contact_details: full contact detail. Requires accountId, contactId.
        - delete_email: delete an email. Requires accountId, emailId. Google trashes it; Microsoft deletes outright.
        - mark_email_read: mark an email read or unread. Requires accountId, emailId, isRead.
        - move_email: move an email to a folder or label. Requires accountId, emailId, destination.
        - delete_event: delete a calendar event. Requires eventId. calendarId optional.
        - create_contact: create a contact. Requires displayName. accountId, givenName, surname, email, phone, jobTitle, companyName, notes optional.
        - update_contact: update a contact. Requires accountId, contactId. Any of displayName, givenName, surname, email, phone, jobTitle, companyName, notes.
        - delete_contact: delete a contact. Requires accountId, contactId.
        - get_email_attachment: fetch one attachment as a resource link (attachment://) plus an attachmentId for send_email. Requires accountId, emailId, attachmentId.
        - get_contextual_email_summary: cluster recent mail into topics. topics, countPerAccount, unreadOnly, includeBodyPreview, maxSamplesPerCluster optional.
        - get_guide: read an in-depth topical guide. Omit name (or pass 'index') for the list.
        - get_unsubscribe_info: report how a mailing can be unsubscribed from. Requires accountId, emailId.
        - unsubscribe_from_email: act on that unsubscribe. Requires accountId, emailId. method 'auto' (default), 'http' or 'mailto'.
        - bulk_delete_emails: delete several emails. Requires items, each with accountId and emailId.
        - bulk_mark_emails_read: mark several emails read or unread. Requires items and isRead.
        - bulk_move_emails: move several emails. Requires items and destination.
        """;

    [McpServerTool, Description(ToolDescription)]
    public async Task<CallToolResult> Calendar(
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Required. The operation to perform -- see the tool description for each action's required and optional fields.")]
        string action,
        [Description("Account id. Optional filter on get_emails/search_emails/list_calendars/get_calendar_events/get_contacts/search_contacts (omit for all accounts); optional target for create_event/create_contact (omit for the first account that permits it) and send_email (omit to match the first recipient's domain, then the first account that may send). Required on get_email_details, get_contact_details, update_contact, delete_contact, delete_email, mark_email_read, move_email, get_email_attachment, get_unsubscribe_info, unsubscribe_from_email. NOT used by the event actions that take an eventId -- the reference names the account. Obtain from list_accounts.")]
        string? accountId = null,
        [Description("Calendar id. Required for get_calendar_event_details and update_event; optional for delete_event/respond_to_event (default 'primary'); optional scoping filter for get_calendar_events; optional target for create_event. Obtain from list_calendars, or pass 'primary' for the default calendar.")]
        string? calendarId = null,
        [Description("Opaque event reference returned as eventId by get_calendar_events or create_event. Required for get_calendar_event_details, update_event, delete_event and respond_to_event. Pass it back unchanged; do not construct or guess one.")]
        string? eventId = null,
        [Description("Email id. Required for get_email_details, delete_email, mark_email_read and move_email. Obtain from the id field returned by get_emails or search_emails.")]
        string? emailId = null,
        [Description("Contact id. Required for get_contact_details. Obtain from get_contacts or search_contacts.")]
        string? contactId = null,
        [Description("Search query. Required for search_emails and search_contacts.")]
        string? query = null,
        [Description("Maximum number of results to return. Applies to get_emails (default 20), search_emails (default 20), get_calendar_events (default 50), get_contacts (default 50), search_contacts (default 50).")]
        int? count = null,
        [Description("get_emails only. If true, only return unread emails. Default false.")]
        bool? unreadOnly = null,
        [Description("get_emails/search_emails. Folder to read instead of the default view: 'inbox', 'archive', 'trash', 'spam', 'drafts', 'sentitems' (aliases 'deleteditems'='trash', 'junkemail'='spam'), or a folder ID (Microsoft), label ID (Google) or folder name (IMAP). Use this to find a message after move_email.")]
        string? folder = null,
        [Description("search_emails only. Only return emails received on or after this date (ISO 8601, e.g. '2026-02-01').")]
        DateTime? fromDate = null,
        [Description("search_emails only. Only return emails received on or before this date (ISO 8601, e.g. '2026-02-28').")]
        DateTime? toDate = null,
        [Description("send_email only. Required. Recipient email address(es) as a JSON array, e.g. [\"alice@example.com\"].")]
        List<string>? to = null,
        [Description("Subject line. Required for send_email and create_event; optional field to change on update_event.")]
        string? subject = null,
        [Description("Body content. send_email: ignored when bodyFormat is 'multipart' (use textBody/htmlBody instead). create_event: optional description.")]
        string? body = null,
        [Description("send_email only. Body content format: 'html' (default), 'text', or 'multipart' (then set textBody and htmlBody instead of body).")]
        string? bodyFormat = null,
        [Description("send_email only. CC recipient email addresses.")]
        List<string>? cc = null,
        [Description("send_email only. Optional file attachments. Each item sets EITHER attachmentId (from a prior upload) OR base64Content (small files only); name is required with base64Content. Total decoded payload per message must stay under 25 MB.")]
        List<OutboundEmailAttachment>? attachments = null,
        [Description("send_email only. Plain-text body for multipart/alternative messages. Required when bodyFormat is 'multipart'.")]
        string? textBody = null,
        [Description("send_email only. HTML body for multipart/alternative messages. Required when bodyFormat is 'multipart'.")]
        string? htmlBody = null,
        [Description("IANA timezone name (e.g. 'America/Chicago', 'Europe/London'). Required for get_calendar_events and get_calendar_event_details; used to create/update events at the correct local time on create_event/update_event.")]
        string? timeZone = null,
        [Description("get_calendar_events only. First local date of the range in timeZone (ISO 8601 date, e.g. '2026-02-20'); any time of day is ignored. Defaults to today in timeZone.")]
        DateTime? startDate = null,
        [Description("get_calendar_events only. Last local date of the range in timeZone, inclusive (ISO 8601 date); any time of day is ignored. Defaults to 6 days after startDate (a 7-day range).")]
        DateTime? endDate = null,
        [Description("create_event/update_event. Event start date and time (ISO 8601); for an all-day event, a date (yyyy-MM-dd). Required for create_event.")]
        DateTime? start = null,
        [Description("create_event/update_event. Event end date and time (ISO 8601); for an all-day event, the exclusive end date (a one-day event on 2026-10-01 ends 2026-10-02). Required for create_event.")]
        DateTime? end = null,
        [Description("create_event/update_event. True for an all-day event: start/end are dates, end exclusive, and any time of day is ignored. update_event: requires both start and end; false makes the event timed; omit to leave it unchanged.")]
        bool? isAllDay = null,
        [Description("create_event/update_event. Event location.")]
        string? location = null,
        [Description("create_event/update_event. List of attendee email addresses.")]
        List<string>? attendees = null,
        [Description("respond_to_event only. Required. One of: 'accept', 'tentative', 'decline'.")]
        string? response = null,
        [Description("respond_to_event only. Optional message to include with the response.")]
        string? comment = null,
        [Description("mark_email_read/bulk_mark_emails_read. Required. True to mark read, false to mark unread.")]
        bool? isRead = null,
        [Description("move_email/bulk_move_emails. Required. Destination: 'archive', 'inbox', 'trash', 'spam', 'drafts', 'sentitems' (aliases 'deleteditems'='trash', 'junkemail'='spam'), or a folder ID (Microsoft), label ID (Google) or folder name (IMAP).")]
        string? destination = null,
        [Description("create_contact/update_contact. Contact display name. Required for create_contact.")]
        string? displayName = null,
        [Description("create_contact/update_contact. Given (first) name.")]
        string? givenName = null,
        [Description("create_contact/update_contact. Surname (last name).")]
        string? surname = null,
        [Description("create_contact/update_contact. Primary email address.")]
        string? email = null,
        [Description("create_contact/update_contact. Primary phone number.")]
        string? phone = null,
        [Description("create_contact/update_contact. Job title.")]
        string? jobTitle = null,
        [Description("create_contact/update_contact. Company name.")]
        string? companyName = null,
        [Description("create_contact/update_contact. Free-text notes.")]
        string? notes = null,
        [Description("get_email_attachment only. Required. Attachment id from get_email_details.")]
        string? attachmentId = null,
        [Description("get_contextual_email_summary only. Comma-separated topics to cluster around; omit to let the summary choose.")]
        string? topics = null,
        [Description("get_contextual_email_summary only. How many emails to scan per account. Default 50.")]
        int? countPerAccount = null,
        [Description("get_contextual_email_summary only. Include a short body preview with each sample. Default false.")]
        bool? includeBodyPreview = null,
        [Description("get_contextual_email_summary only. Maximum sample emails shown per cluster. Default 5.")]
        int? maxSamplesPerCluster = null,
        [Description("get_guide only. Guide name; omit (or pass 'index') for the list of available guides.")]
        string? name = null,
        [Description("unsubscribe_from_email only. Unsubscribe method: 'auto' (default), 'http', or 'mailto'.")]
        string? method = null,
        [Description("bulk_delete_emails/bulk_mark_emails_read/bulk_move_emails. Required. The emails to act on, each item carrying its accountId and emailId.")]
        BulkEmailItem[]? items = null)
    {
        IDisposable tenantScope;
        try
        {
            tenantScope = _tenantContext.Bind(TenantIdentity.FromPrincipal(requestContext.User));
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
        using (tenantScope)
        {
            var text = await DispatchAction(action, new CalendarActionArguments
            {
                AccountId = accountId,
                CalendarId = calendarId,
                EventId = eventId,
                EmailId = emailId,
                ContactId = contactId,
                Query = query,
                Count = count,
                UnreadOnly = unreadOnly,
                Folder = folder,
                FromDate = fromDate,
                ToDate = toDate,
                To = to,
                Subject = subject,
                Body = body,
                BodyFormat = bodyFormat,
                Cc = cc,
                Attachments = attachments,
                TextBody = textBody,
                HtmlBody = htmlBody,
                TimeZone = timeZone,
                StartDate = startDate,
                EndDate = endDate,
                Start = start,
                End = end,
                IsAllDay = isAllDay,
                Location = location,
                Attendees = attendees,
                Response = response,
                Comment = comment,
                IsRead = isRead,
                Destination = destination,
                DisplayName = displayName,
                GivenName = givenName,
                Surname = surname,
                Email = email,
                Phone = phone,
                JobTitle = jobTitle,
                CompanyName = companyName,
                Notes = notes,
                AttachmentId = attachmentId,
                Topics = topics,
                CountPerAccount = countPerAccount,
                IncludeBodyPreview = includeBodyPreview,
                MaxSamplesPerCluster = maxSamplesPerCluster,
                Name = name,
                Method = method,
                Items = items,
            }).ConfigureAwait(false);
            return action == "get_email_attachment" ? WithAttachmentLink(text) : TextResult(text);
        }
    }

    internal Task<string> DispatchAction(string action, CalendarActionArguments args)
    {
        if (!ActionNames.Contains(action, StringComparer.Ordinal))
        {
            throw UnknownAction(action);
        }

        return action switch
        {
            "list_accounts" => ListAccountsAction(),
            "get_emails" => GetEmailsAction(args.AccountId, args.Count, args.UnreadOnly, args.Folder),
            "get_email_details" => GetEmailDetailsAction(args.AccountId, args.EmailId),
            "search_emails" => SearchEmailsAction(args.Query, args.AccountId, args.Count, args.FromDate, args.ToDate, args.Folder),
            "list_calendars" => ListCalendarsAction(args.AccountId),
            "get_calendar_events" => GetCalendarEventsAction(args.TimeZone, args.StartDate, args.EndDate, args.AccountId, args.CalendarId, args.Count),
            "get_calendar_event_details" => GetCalendarEventDetailsAction(args.TimeZone, args.CalendarId, args.EventId),
            "get_contacts" => GetContactsAction(args.AccountId, args.Count),
            "search_contacts" => SearchContactsAction(args.Query, args.AccountId, args.Count),
            "get_contact_details" => GetContactDetailsAction(args.AccountId, args.ContactId),
            "create_event" => CreateEventAction(args.Subject, args.Start, args.End, args.AccountId, args.CalendarId, args.Location, args.Attendees, args.Body, args.TimeZone, args.IsAllDay),
            "update_event" => UpdateEventAction(args.CalendarId, args.EventId, args.Subject, args.Start, args.End, args.Location, args.Attendees, args.TimeZone, args.IsAllDay),
            "respond_to_event" => RespondToEventAction(args.EventId, args.Response, args.CalendarId, args.Comment),
            "send_email" => SendEmailAction(args.To, args.Subject, args.Body, args.AccountId, args.BodyFormat, args.Cc, args.Attachments, args.TextBody, args.HtmlBody),
            "delete_email" => DeleteEmailAction(args.AccountId, args.EmailId),
            "mark_email_read" => MarkEmailReadAction(args.AccountId, args.EmailId, args.IsRead),
            "move_email" => MoveEmailAction(args.AccountId, args.EmailId, args.Destination),
            "delete_event" => DeleteEventAction(args.EventId, args.CalendarId),
            "create_contact" => CreateContactAction(args.DisplayName, args.AccountId, args.GivenName, args.Surname, args.Email, args.Phone, args.JobTitle, args.CompanyName, args.Notes),
            "update_contact" => UpdateContactAction(args.AccountId, args.ContactId, args.DisplayName, args.GivenName, args.Surname, args.Email, args.Phone, args.JobTitle, args.CompanyName, args.Notes),
            "delete_contact" => DeleteContactAction(args.AccountId, args.ContactId),
            "get_email_attachment" => GetEmailAttachmentAction(args.AccountId, args.EmailId, args.AttachmentId),
            "get_contextual_email_summary" => GetContextualEmailSummaryAction(args.Topics, args.CountPerAccount, args.UnreadOnly, args.IncludeBodyPreview, args.MaxSamplesPerCluster),
            "get_guide" => GetGuideAction(args.Name),
            "get_unsubscribe_info" => GetUnsubscribeInfoAction(args.AccountId, args.EmailId),
            "unsubscribe_from_email" => UnsubscribeFromEmailAction(args.AccountId, args.EmailId, args.Method),
            "bulk_delete_emails" => BulkDeleteEmailsAction(args.Items),
            "bulk_mark_emails_read" => BulkMarkEmailsReadAction(args.Items, args.IsRead),
            "bulk_move_emails" => BulkMoveEmailsAction(args.Items, args.Destination),
            _ => throw UnknownAction(action),
        };
    }

    /// <summary>
    /// Never a default action, never a protocol error: names the bad value
    /// and lists every valid action so the caller can self-correct (T-46-18).
    /// </summary>
    private static McpException UnknownAction(string action) =>
        new($"Unknown action '{action}'. Valid actions are: {string.Join(", ", ActionNames)}.");

    internal static object CreateInstance(IServiceProvider? services) =>
        services is not null
            ? ActivatorUtilities.CreateInstance(services, typeof(CalendarActionTool))
            : throw new InvalidOperationException("CalendarActionTool requires a service provider.");

    /// <summary>
    /// Injects the <c>action</c> property's JSON schema <c>enum</c>
    /// constraint into an already-built tool's input schema, in place.
    /// </summary>
    /// <remarks>
    /// <c>action</c> is bound as a plain <see cref="string"/> (see
    /// <see cref="Calendar"/>), not a C# enum type, so an unrecognized value
    /// is rejected by <see cref="UnknownAction"/> with a message naming the
    /// value and listing the valid ones, instead of a generic
    /// JSON-deserialization failure. That rules out getting the schema
    /// <c>enum</c> "for free" from the parameter's own CLR type.
    /// <para>
    /// <see cref="Microsoft.Extensions.AI.AIJsonSchemaCreateOptions.TransformSchemaNode"/>
    /// was tried first and does not work for this: measured live (build +
    /// run + tools/list), <c>AIJsonSchemaCreateContext</c> carries an empty
    /// <c>Path</c> and a null <c>PropertyInfo</c> when the schema comes from
    /// <em>function</em>-parameter generation (as opposed to a POCO type
    /// graph) -- every parameter's per-parameter schema is generated
    /// independently with no signal tying it back to its parameter name, so
    /// there is nothing to match "this node is the action property" against.
    /// Patching the fully-assembled <see cref="Tool.InputSchema"/> after
    /// construction sidesteps that gap entirely.
    /// </para>
    /// </remarks>
    internal static void PatchActionEnumIntoSchema(Tool protocolTool)
    {
        var schema = JsonNode.Parse(protocolTool.InputSchema.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("Calendar tool input schema failed to parse.");
        var properties = schema["properties"]?.AsObject()
            ?? throw new InvalidOperationException("Calendar tool input schema has no 'properties'.");
        var actionProperty = properties["action"]?.AsObject()
            ?? throw new InvalidOperationException("Calendar tool input schema has no 'action' property.");

        actionProperty["enum"] = new JsonArray(ActionNames.Select(a => (JsonNode)a).ToArray());

        protocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
    }
}

/// <summary>
/// Registers <see cref="CalendarActionTool"/>'s single curated tool.
/// </summary>
/// <remarks>
/// Built via <c>McpServerTool.Create(MethodInfo, Func&lt;RequestContext&lt;CallToolRequestParams&gt;, object&gt;, McpServerToolCreateOptions)</c>
/// rather than the attribute-scanning <c>WithTools&lt;T&gt;()</c> path --
/// mirroring the SDK's own internal implementation of
/// <c>WithTools&lt;T&gt;()</c> (<c>McpServerBuilderExtensions.CreateTarget</c>)
/// -- so the resulting <see cref="McpServerTool.ProtocolTool"/> can be
/// patched with the <c>action</c> schema <c>enum</c>
/// (<see cref="CalendarActionTool.PatchActionEnumIntoSchema"/>) before it is
/// handed to the DI container.
/// </remarks>
public static class CalendarActionToolServiceExtensions
{
    public static IMcpServerBuilder WithCalendarActionTool(this IMcpServerBuilder builder)
    {
        var method = typeof(CalendarActionTool).GetMethod(nameof(CalendarActionTool.Calendar))
            ?? throw new InvalidOperationException("CalendarActionTool.Calendar method not found.");

        builder.Services.AddSingleton<McpServerTool>(services =>
        {
            var tool = McpServerTool.Create(
                method,
                r => CalendarActionTool.CreateInstance(r.Services),
                new McpServerToolCreateOptions { Services = services });

            CalendarActionTool.PatchActionEnumIntoSchema(tool.ProtocolTool);

            // Bind the MCP Apps view. Set here rather than via [McpAppUi] because the tool is
            // built by hand in this factory, and its _meta belongs beside the schema patch that
            // is already applied here instead of in a second, attribute-driven mechanism.
#pragma warning disable MCPEXP003 // MCP Apps (SEP-1865) is experimental; see Apps/CalendarView.cs.
            McpApps.SetAppUi(tool, new McpUiToolMeta { ResourceUri = CalendarView.ResourceUri });
#pragma warning restore MCPEXP003

            return tool;
        });

        return builder;
    }

    /// <summary>
    /// Registers this fork's whole calendar-mcp surface: the curated <c>calendar</c> tool, its
    /// MCP Apps view, the <c>attachment://stash/{id}</c> resource, and the three prompt classes.
    /// Both hosts (<c>StdioServer/Program.cs</c>, <c>HttpServer/Program.cs</c>) and the test
    /// harness (<c>InProcessMcpSession</c>) call this one extension instead of the six calls
    /// individually, so all three build the same registration chain -- a chain a future upstream
    /// sync's <c>Program.cs</c> merge conflict cannot silently narrow the way six separate calls
    /// could, and one the test suite actually exercises.
    /// </summary>
    public static IMcpServerBuilder WithCalendarMcpSurface(this IMcpServerBuilder builder) =>
        builder
            .WithCalendarActionTool()
            // The MCP Apps view (ui://calendar/view.html). The tool's own _meta.ui is
            // set in WithCalendarActionTool's factory, beside the schema patch.
            .WithCalendarView()
            // attachment://stash/{id}: the file behind get_email_attachment's resource_link.
            .WithEmailAttachmentResource()
            .WithPrompts<CalendarMcp.Core.Prompts.CalendarPrompts>()
            .WithPrompts<CalendarMcp.Core.Prompts.EmailPrompts>()
            .WithPrompts<CalendarMcp.Core.Prompts.ContactPrompts>();
}
