using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// The actions whose contract differs from upstream's: a calendar event is addressed by the
/// opaque <see cref="EventRef"/> (MCP-05, D-20) instead of the provider's raw id plus an accountId.
/// get_calendar_events and create_event mint references; get_calendar_event_details,
/// update_event, delete_event and respond_to_event take one. Every action forwards to the
/// upstream implementation and rewrites only the id fields of its JSON, so every other change
/// upstream makes to these tools -- permissions, all-day ranges, local-day windows -- arrives here
/// without a second copy of the logic.
/// </summary>
public sealed partial class CalendarActionTool
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>get_calendar_events, with each event's provider id replaced by its EventRef.</summary>
    private async Task<string> GetCalendarEventsAction(
        string? timeZone, DateTime? startDate, DateTime? endDate, string? accountId, string? calendarId, int? count)
    {
        // Required by upstream's own schema; optional in the multiplexed one.
        ToolGuard.RequireNonEmpty(timeZone, nameof(timeZone));

        var json = await Impl<GetCalendarEventsTool>().GetCalendarEvents(
            timeZone!, startDate, endDate, accountId, calendarId, count ?? 50);
        return WithEventRefs(json);
    }

    /// <summary>get_calendar_event_details: the account is resolved from the reference.</summary>
    private async Task<string> GetCalendarEventDetailsAction(string? timeZone, string? calendarId, string? eventId)
    {
        ToolGuard.RequireNonEmpty(timeZone, nameof(timeZone));
        ToolGuard.RequireNonEmpty(calendarId, nameof(calendarId));

        var json = await ForwardWithEventRef(eventId, (accountId, rawEventId) =>
            Impl<GetCalendarEventDetailsTool>().GetCalendarEventDetails(timeZone!, accountId, calendarId!, rawEventId));

        // The reference is echoed back byte-for-byte; the raw ids it wraps are never shown.
        var details = ParseObject(json);
        RequireString(details, "id", "get_calendar_event_details");
        RequireString(details, "accountId", "get_calendar_event_details");
        RequireAbsent(details, "eventId", "get_calendar_event_details");
        var rewritten = new JsonObject { ["eventId"] = eventId };
        foreach (var (key, value) in details)
        {
            if (key is not ("id" or "accountId"))
                rewritten[key] = value?.DeepClone();
        }
        return rewritten.ToJsonString(IndentedJson);
    }

    private async Task<string> CreateEventAction(
        string? subject, DateTime? start, DateTime? end, string? accountId, string? calendarId,
        string? location, List<string>? attendees, string? body, string? timeZone, bool? isAllDay)
    {
        if (string.IsNullOrEmpty(subject))
            throw new McpException("subject is required.");
        if (start is null)
            throw new McpException("start is required.");
        if (end is null)
            throw new McpException("end is required.");

        var result = ParseObject(await Impl<CreateEventTool>().CreateEvent(
            subject, start.Value, end.Value, accountId, calendarId, location, attendees, body, timeZone, isAllDay ?? false));
        result["eventId"] = EventRef.Encode(
            RequireString(result, "accountUsed", "create_event"), RequireString(result, "eventId", "create_event"));
        return result.ToJsonString(IndentedJson);
    }

    private async Task<string> UpdateEventAction(
        string? calendarId, string? eventId, string? subject, DateTime? start, DateTime? end,
        string? location, List<string>? attendees, string? timeZone, bool? isAllDay) =>
        EchoEventRef(await ForwardWithEventRef(eventId, (accountId, rawEventId) =>
            Impl<UpdateEventTool>().UpdateEvent(
                accountId, calendarId!, rawEventId, subject, start, end, location, attendees, timeZone, isAllDay)),
            eventId!, "update_event");

    private async Task<string> DeleteEventAction(string? eventId, string? calendarId) =>
        EchoEventRef(await ForwardWithEventRef(eventId, (accountId, rawEventId) =>
            Impl<DeleteEventTool>().DeleteEvent(rawEventId, accountId, calendarId)),
            eventId!, "delete_event");

    private async Task<string> RespondToEventAction(
        string? eventId, string? response, string? calendarId, string? comment) =>
        EchoEventRef(await ForwardWithEventRef(eventId, (accountId, rawEventId) =>
            Impl<RespondToEventTool>().RespondToEvent(rawEventId, response!, accountId, calendarId, comment)),
            eventId!, "respond_to_event");

    /// <summary>
    /// Decodes the reference and runs the upstream call with the account and raw id it wraps. A
    /// missing or malformed reference is rejected, never resolved against a default account, and
    /// an error that quotes the raw id quotes the reference instead: the caller only holds that.
    /// </summary>
    private static async Task<string> ForwardWithEventRef(
        string? reference, Func<string, string, Task<string>> call)
    {
        ToolGuard.RequireNonEmpty(reference, "eventId");
        if (!EventRef.TryDecode(reference, out var accountId, out var rawEventId))
        {
            throw new McpException(
                "eventId is not a valid event reference. Obtain it from the eventId field returned by get_calendar_events or create_event -- do not construct or guess one.");
        }

        try
        {
            return await call(accountId, rawEventId);
        }
        catch (McpException ex) when (ex.Message.Contains(rawEventId, StringComparison.Ordinal))
        {
            throw new McpException(ex.Message.Replace(rawEventId, reference, StringComparison.Ordinal), ex);
        }
    }

    /// <summary>Replaces the raw eventId a write result echoes with the caller's reference.</summary>
    private static string EchoEventRef(string upstreamJson, string reference, string action)
    {
        var result = ParseObject(upstreamJson);
        RequireString(result, "eventId", action);
        result["eventId"] = reference;
        return result.ToJsonString(IndentedJson);
    }

    /// <summary>
    /// Replaces <c>events[].id</c> with <c>eventId</c> = EventRef(accountId, id). Fails loudly if
    /// upstream renames either field or starts emitting its own <c>eventId</c>: a silent
    /// pass-through would hand the model raw ids that every event action then rejects.
    /// </summary>
    internal static string WithEventRefs(string upstreamJson)
    {
        var response = ParseObject(upstreamJson);
        if (response["events"] is not JsonArray events)
            throw new InvalidOperationException("get_calendar_events returned no 'events' array.");

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i]?.AsObject()
                ?? throw new InvalidOperationException($"get_calendar_events returned a null event at index {i}.");
            RequireAbsent(evt, "eventId", "get_calendar_events");
            var reference = EventRef.Encode(
                RequireString(evt, "accountId", "get_calendar_events"), RequireString(evt, "id", "get_calendar_events"));

            var rewritten = new JsonObject { ["eventId"] = reference };
            foreach (var (key, value) in evt)
            {
                if (key != "id")
                    rewritten[key] = value?.DeepClone();
            }
            events[i] = rewritten;
        }
        return response.ToJsonString(IndentedJson);
    }

    private static string RequireString(JsonObject result, string field, string action) =>
        result[field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : throw new InvalidOperationException($"{action} returned no '{field}' string.");

    private static void RequireAbsent(JsonObject result, string field, string action)
    {
        if (result.ContainsKey(field))
            throw new InvalidOperationException($"{action} now returns its own '{field}'; the EventRef rewrite would hide it.");
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Calendar tool returned a non-object JSON result.");
}
