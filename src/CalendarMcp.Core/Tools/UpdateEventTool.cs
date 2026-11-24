using System.ComponentModel;
using System.Text.Json;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// MCP tool for updating calendar events
/// </summary>
[McpServerToolType]
public sealed class UpdateEventTool(
    IAccountRegistry accountRegistry,
    IProviderServiceFactory providerFactory,
    ILogger<UpdateEventTool> logger)
{
    [McpServerTool, Description("Update an existing calendar event. Always pass the timeZone parameter using the user's local IANA timezone (e.g. `America/Chicago`, `America/New_York`, `Europe/London`) when updating start/end times so events are updated at the correct local time.")]
    public async Task<string> UpdateEvent(
        [Description("Calendar ID that contains the event. Obtain from list_calendars.")] string calendarId,
        [Description("The eventId reference returned by get_calendar_events or create_event, passed back byte for byte. It identifies the account.")] string eventId,
        [Description("New event subject/title")] string? subject = null,
        [Description("New event start date and time (ISO 8601 format). For an all-day event, a date (yyyy-MM-dd).")] DateTime? start = null,
        [Description("New event end date and time (ISO 8601 format). For an all-day event, the exclusive end date (yyyy-MM-dd).")] DateTime? end = null,
        [Description("New event location")] string? location = null,
        [Description("New list of attendee email addresses")] List<string>? attendees = null,
        [Description("IANA timezone name for the event times (e.g. `America/Chicago`, `America/New_York`, `Europe/London`). Required when updating start or end times.")] string? timeZone = null,
        [Description("Set true to make the event all-day (start/end become dates, end exclusive) or false to make it timed. Requires both start and end. Pass true when moving an existing all-day event. Omit to leave it unchanged.")] bool? isAllDay = null)
    {
        logger.LogInformation("Updating event: eventId={EventId}, calendarId={CalendarId}",
            eventId, calendarId);

        ToolGuard.RequireNonEmpty(calendarId, nameof(calendarId));
        var (accountId, rawEventId) = EventRef.Resolve(eventId);

        if (isAllDay.HasValue)
        {
            if (!start.HasValue || !end.HasValue)
                throw new McpException("isAllDay requires both start and end.");

            if (isAllDay.Value)
            {
                var (allDayStart, allDayEnd) = AllDayRange.Normalize(start.Value, end.Value);
                (start, end) = (allDayStart, allDayEnd);
            }
        }

        var account = await ToolGuard.RequireAccountAsync(
            accountRegistry, accountId, AccountPermission.CalendarWrite);

        try
        {
            var provider = providerFactory.GetProvider(account.Provider);
            await provider.UpdateEventAsync(
                accountId, calendarId, rawEventId, subject, start, end, location, attendees, timeZone, isAllDay, CancellationToken.None);

            return JsonSerializer.Serialize(new
            {
                success = true,
                eventId,
                accountUsed = accountId,
                calendarUsed = calendarId
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (McpException ex) when (EventRef.Quotes(ex, rawEventId))
        {
            throw EventRef.Requote(ex, eventId, rawEventId);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error in update_event tool");
            throw ToolGuard.Failure("update event", ex);
        }
    }
}
