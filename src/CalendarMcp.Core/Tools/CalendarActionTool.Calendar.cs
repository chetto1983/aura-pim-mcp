using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// The event actions. The upstream classes already speak <see cref="EventRef"/> (MCP-05, D-20):
/// get_calendar_events and create_event return a reference, get_calendar_event_details,
/// update_event, delete_event and respond_to_event take one. Each action only fills in what the
/// multiplexed schema leaves optional and forwards.
/// </summary>
public sealed partial class CalendarActionTool
{
    private Task<string> GetCalendarEventsAction(
        string? timeZone, DateTime? startDate, DateTime? endDate, string? accountId, string? calendarId, int? count)
    {
        // Required by upstream's own schema; optional in the multiplexed one.
        ToolGuard.RequireNonEmpty(timeZone, nameof(timeZone));

        return Impl<GetCalendarEventsTool>().GetCalendarEvents(
            timeZone!, startDate, endDate, accountId, calendarId, count ?? 50);
    }

    private Task<string> GetCalendarEventDetailsAction(string? timeZone, string? calendarId, string? eventId)
    {
        ToolGuard.RequireNonEmpty(timeZone, nameof(timeZone));
        ToolGuard.RequireNonEmpty(calendarId, nameof(calendarId));

        return Impl<GetCalendarEventDetailsTool>().GetCalendarEventDetails(timeZone!, calendarId!, eventId!);
    }

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
        string? calendarId, string? eventId, string? subject, DateTime? start, DateTime? end,
        string? location, List<string>? attendees, string? timeZone, bool? isAllDay) =>
        Impl<UpdateEventTool>().UpdateEvent(
            calendarId!, eventId!, subject, start, end, location, attendees, timeZone, isAllDay);

    private Task<string> DeleteEventAction(string? eventId, string? calendarId) =>
        Impl<DeleteEventTool>().DeleteEvent(eventId!, calendarId);

    private Task<string> RespondToEventAction(
        string? eventId, string? response, string? calendarId, string? comment) =>
        Impl<RespondToEventTool>().RespondToEvent(eventId!, response!, calendarId, comment);

    private static string RequireString(JsonObject result, string field, string action) =>
        result[field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : throw new InvalidOperationException($"{action} returned no '{field}' string.");

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Calendar tool returned a non-object JSON result.");
}
