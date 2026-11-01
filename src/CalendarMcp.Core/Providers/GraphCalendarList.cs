using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Reads every calendar of a Graph account. A collection answers one page at a time and points
/// at the next with <c>@odata.nextLink</c>, so one request could leave calendars out.
/// </summary>
internal static class GraphCalendarList
{
    public static async Task<List<Calendar>> ReadAllAsync(GraphServiceClient client, CancellationToken cancellationToken)
    {
        var calendars = new List<Calendar>();
        var page = await client.Me.Calendars.GetAsync(config =>
        {
            config.QueryParameters.Select = ["id", "name", "owner", "canEdit", "isDefaultCalendar", "hexColor"];
        }, cancellationToken);

        while (page is not null)
        {
            calendars.AddRange(page.Value ?? []);
            page = page.OdataNextLink is { } next
                ? await client.Me.Calendars.WithUrl(next).GetAsync(cancellationToken: cancellationToken)
                : null;
        }

        return calendars;
    }
}
