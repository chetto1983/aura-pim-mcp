using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// The two actions whose contract differs from upstream's: calendar events are addressed by the
/// opaque <see cref="EventRef"/> (MCP-05, D-20) instead of the provider's raw id. Both forward to
/// the upstream implementation and rewrite only the id fields of its JSON, so every other change
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

    /// <summary>
    /// get_calendar_event_details: no accountId argument. The account is resolved from the EventRef
    /// that get_calendar_events returned, and a missing or malformed reference is rejected as a
    /// plain validation failure, never resolved against a default account.
    /// </summary>
    private async Task<string> GetCalendarEventDetailsAction(string? timeZone, string? calendarId, string? eventId)
    {
        ToolGuard.RequireNonEmpty(timeZone, nameof(timeZone));
        ToolGuard.RequireNonEmpty(calendarId, nameof(calendarId));
        ToolGuard.RequireNonEmpty(eventId, nameof(eventId));

        if (!EventRef.TryDecode(eventId, out var accountId, out var rawEventId))
        {
            throw new McpException(
                "eventId is not a valid event reference. Obtain it from the eventId field returned by get_calendar_events -- do not construct or guess one.");
        }

        var json = await Impl<GetCalendarEventDetailsTool>().GetCalendarEventDetails(
            timeZone!, accountId, calendarId!, rawEventId);

        // The reference is echoed back byte-for-byte; the raw ids it wraps are never shown.
        var details = ParseObject(json);
        var rewritten = new JsonObject { ["eventId"] = eventId };
        foreach (var (key, value) in details)
        {
            if (key is not ("id" or "accountId"))
                rewritten[key] = value?.DeepClone();
        }
        return rewritten.ToJsonString(IndentedJson);
    }

    /// <summary>
    /// Replaces <c>events[].id</c> with <c>eventId</c> = EventRef(accountId, id). Fails loudly if
    /// upstream renames either field: a silent pass-through would hand the model raw ids that
    /// get_calendar_event_details then rejects.
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
            var id = evt["id"]?.GetValue<string>();
            var accountId = evt["accountId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accountId))
                throw new InvalidOperationException("get_calendar_events returned an event without 'id' and 'accountId'.");

            var rewritten = new JsonObject { ["eventId"] = EventRef.Encode(accountId, id) };
            foreach (var (key, value) in evt)
            {
                if (key != "id")
                    rewritten[key] = value?.DeepClone();
            }
            events[i] = rewritten;
        }
        return response.ToJsonString(IndentedJson);
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Calendar tool returned a non-object JSON result.");
}
