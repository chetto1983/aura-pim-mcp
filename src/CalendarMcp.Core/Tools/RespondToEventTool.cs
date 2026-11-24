using System.ComponentModel;
using System.Text.Json;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// MCP tool for responding to calendar event invitations (accept, tentative, decline)
/// </summary>
[McpServerToolType]
public sealed class RespondToEventTool(
    IAccountRegistry accountRegistry,
    IProviderServiceFactory providerFactory,
    ILogger<RespondToEventTool> logger)
{
    [McpServerTool, Description("Accept, tentatively accept, or decline a calendar event invitation. Pass the eventId reference from get_calendar_events; it identifies the account.")]
    public async Task<string> RespondToEvent(
        [Description("The eventId reference returned by get_calendar_events or create_event, passed back byte for byte. It identifies the account.")] string eventId,
        [Description("Response: 'accept', 'tentative', or 'decline'")] string response,
        [Description("Calendar ID containing the event, or omit for default calendar")] string? calendarId = null,
        [Description("Optional message to include with the response")] string? comment = null)
    {
        logger.LogInformation("Responding to event: eventId={EventId}, response={Response}, calendarId={CalendarId}",
            eventId, response, calendarId);

        var (accountId, rawEventId) = EventRef.Resolve(eventId);
        ToolGuard.RequireNonEmpty(response, nameof(response));

        // Validate response type
        var normalizedResponse = response.ToLowerInvariant();
        if (normalizedResponse != "accept" && normalizedResponse != "accepted" &&
            normalizedResponse != "tentative" && normalizedResponse != "tentativelyaccepted" &&
            normalizedResponse != "decline" && normalizedResponse != "declined")
        {
            throw new McpException("Invalid response type. Valid values are: accept, tentative, decline");
        }

        var account = await ToolGuard.RequireAccountAsync(
            accountRegistry, accountId, Models.AccountPermission.CalendarWrite);

        try
        {
            // Respond to event
            var provider = providerFactory.GetProvider(account.Provider);
            await provider.RespondToEventAsync(
                account.Id, calendarId ?? "primary", rawEventId, response, comment, CancellationToken.None);

            var result = new
            {
                success = true,
                message = $"Event response sent: {response}",
                eventId = eventId,
                response = normalizedResponse,
                accountUsed = account.Id,
                calendarUsed = calendarId ?? "default"
            };

            logger.LogInformation("Responded to event {EventId} with {Response} from account {AccountId}", 
                eventId, response, account.Id);

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error in respond_to_event tool");
            throw EventRef.Requote(ToolGuard.Failure("respond to event", ex), eventId, rawEventId);
        }
    }
}
