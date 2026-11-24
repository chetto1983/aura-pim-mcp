using System.ComponentModel;
using System.Text.Json;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// MCP tool for deleting calendar events
/// </summary>
[McpServerToolType]
public sealed class DeleteEventTool(
    IAccountRegistry accountRegistry,
    IProviderServiceFactory providerFactory,
    ILogger<DeleteEventTool> logger)
{
    [McpServerTool, Description("Delete a calendar event. Pass the eventId reference from get_calendar_events or create_event; it identifies the account.")]
    public async Task<string> DeleteEvent(
        [Description("The eventId reference returned by get_calendar_events or create_event, passed back byte for byte. It identifies the account.")] string eventId,
        [Description("Calendar ID containing the event, or omit for default calendar")] string? calendarId = null)
    {
        logger.LogInformation("Deleting event: eventId={EventId}, calendarId={CalendarId}",
            eventId, calendarId);

        var (accountId, rawEventId) = EventRef.Resolve(eventId);

        var account = await ToolGuard.RequireAccountAsync(
            accountRegistry, accountId, Models.AccountPermission.CalendarWrite);

        try
        {
            // Delete event
            var provider = providerFactory.GetProvider(account.Provider);
            await provider.DeleteEventAsync(
                account.Id, calendarId ?? "primary", rawEventId, CancellationToken.None);

            var result = new
            {
                success = true,
                message = "Event deleted successfully",
                eventId = eventId,
                accountUsed = account.Id,
                calendarUsed = calendarId ?? "default"
            };

            logger.LogInformation("Deleted event {EventId} from account {AccountId}", eventId, account.Id);

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (McpException ex) when (EventRef.Quotes(ex, rawEventId))
        {
            throw EventRef.Requote(ex, eventId, rawEventId);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error in delete_event tool");
            throw ToolGuard.Failure("delete event", ex);
        }
    }
}
