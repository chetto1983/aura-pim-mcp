using System.Globalization;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using ChangedEvents = Microsoft.Graph.Me.Calendars.Item.CalendarView.Delta.DeltaGetResponse;
using ChangeType = CalendarMcp.Core.Models.ChangeType;

namespace CalendarMcp.Core.Changes.Graph;

/// <summary>
/// Microsoft calendars through calendar view delta, one position per calendar the account can edit
/// (https://learn.microsoft.com/en-us/graph/delta-query-events). Delta over a calendar with no date
/// range is beta only, so each position watches a fixed window, and its token keeps that window for life.
/// </summary>
/// <remarks>
/// The reference page names only the primary calendar. The concept page says "some other specified
/// calendar" and the SDK generates this path, so this rests on those until a real mailbox runs it.
/// </remarks>
internal sealed class GraphCalendarChanges(string provider, IGraphApiClients clients, TimeProvider clock) : IChangesSource
{
    private const string Removed = "@removed";

    private static readonly TimeSpan WindowBehind = TimeSpan.FromDays(1);

    /// <summary>Further ahead than anyone plans a meeting they could need telling about today.</summary>
    private static readonly TimeSpan WindowAhead = TimeSpan.FromDays(400);

    /// <summary>A window with less than this ahead of it is replaced, long before an event could fall off its end.</summary>
    private static readonly TimeSpan WindowKept = TimeSpan.FromDays(200);

    private static readonly string[] EventFields =
    [
        "id", "subject", "start", "end", "isAllDay", "isCancelled", "isOrganizer", "organizer",
        "responseStatus", "createdDateTime", "lastModifiedDateTime"
    ];

    private string Kind => ChangesCursor.KindOf(provider, ChangeResource.Calendar);

    public string Provider => provider;

    public string Resource => ChangeResource.Calendar;

    public async Task<ChangesCursor> StartAsync(string accountId, CancellationToken cancellationToken)
    {
        var graph = await clients.GraphAsync(accountId, cancellationToken);
        var now = clock.GetUtcNow();

        var positions = new Dictionary<string, Position>();
        foreach (var id in await EditableCalendarsAsync(graph, cancellationToken))
            positions[id] = await OpenAsync(graph, id, now, cancellationToken);

        return ChangesCursor.Of(Kind, positions);
    }

    public async Task<ChangesBatch> ReadAsync(
        string accountId, ChangesCursor cursor, int limit, CancellationToken cancellationToken)
    {
        var saved = cursor.Folders<Position>();
        foreach (var saving in saved.Values)
        {
            GraphLinks.Checked(saving.Link);
            if (saving.Next is not null)
                GraphLinks.Checked(saving.Next.Link);
        }

        var graph = await clients.GraphAsync(accountId, cancellationToken);
        var now = clock.GetUtcNow();

        var positions = new Dictionary<string, Position>();
        var changes = new List<Change>();

        foreach (var id in await EditableCalendarsAsync(graph, cancellationToken))
        {
            if (!saved.TryGetValue(id, out var position))
            {
                // A calendar the account just gained has nothing new in it yet, whatever it holds.
                positions[id] = await OpenAsync(graph, id, now, cancellationToken);
            }
            else if (changes.Count >= limit)
            {
                positions[id] = position with { RoundDone = false };
            }
            else
            {
                try
                {
                    positions[id] = await AdvanceAsync(graph, id, position, limit, now, changes, cancellationToken);
                }
                catch (ApiException e) when (GraphLinks.IsExpired(e))
                {
                    throw new CursorExpiredException(ChangesCursor.Of(Kind, new Dictionary<string, Position>(saved)
                    {
                        [id] = await OpenAsync(graph, id, now, cancellationToken)
                    }));
                }
            }
        }

        return new ChangesBatch(changes, ChangesCursor.Of(Kind, positions), positions.Values.Any(p => !p.RoundDone));
    }

    public async Task<ItemFacts?> ReadItemAsync(string accountId, ItemRef item, CancellationToken cancellationToken)
    {
        var graph = await clients.GraphAsync(accountId, cancellationToken);

        var found = await ReadEventAsync(graph, item.CalendarId!, item.Id!, cancellationToken);
        if (found is null)
            return null;

        // The id it was asked for, not the one Graph answers with: a verdict older than the
        // lasting ids is found by the id it holds.
        return new ItemFacts
        {
            Id = item.Id!,
            CalendarId = item.CalendarId,
            OccurredAt = found.LastModifiedDateTime,
            From = found.Organizer?.EmailAddress?.Address,
            Subject = found.Subject,
            Event = Facts(found)
        };
    }

    private static async Task<IEnumerable<string>> EditableCalendarsAsync(GraphServiceClient graph, CancellationToken cancellationToken) =>
        (await GraphCalendarList.ReadAllAsync(graph, cancellationToken))
        .Where(calendar => calendar.CanEdit == true)
        .Select(calendar => calendar.Id!);

    private static async Task<Position> OpenAsync(
        GraphServiceClient graph, string calendarId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var windowEnd = now + WindowAhead;
        var page = await graph.Me.Calendars[calendarId].CalendarView.Delta.GetAsDeltaGetResponseAsync(config =>
        {
            config.QueryParameters.StartDateTime = EventChanges.Instant(now - WindowBehind);
            config.QueryParameters.EndDateTime = EventChanges.Instant(windowEnd);
            Prefer(config.Headers, ChangesService.MaxLimit);
        }, cancellationToken);

        // The first round is the whole window, so it is walked for its token and thrown away.
        while (page?.OdataDeltaLink is null)
        {
            var next = page?.OdataNextLink ?? throw ChangesException.ProviderTransient();
            page = await PageAsync(graph, calendarId, next, ChangesService.MaxLimit, cancellationToken);
        }

        return new Position(page.OdataDeltaLink, null, now, windowEnd, true);
    }

    private static Task<ChangedEvents?> PageAsync(
        GraphServiceClient graph, string calendarId, string link, int pageSize, CancellationToken cancellationToken) =>
        graph.Me.Calendars[calendarId].CalendarView.Delta.WithUrl(GraphLinks.Checked(link))
            .GetAsDeltaGetResponseAsync(config => Prefer(config.Headers, pageSize), cancellationToken);

    private static void Prefer(RequestHeaders headers, int pageSize)
    {
        headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
        headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        GraphLinks.AskForLastingIds(headers);
    }

    /// <summary>
    /// Walks a calendar's round until it ends or the page is full. A window running short gets its
    /// successor before the last read of the old one, so the two overlap instead of leaving a gap.
    /// </summary>
    private static async Task<Position> AdvanceAsync(
        GraphServiceClient graph, string calendarId, Position position, int limit, DateTimeOffset now,
        List<Change> changes, CancellationToken cancellationToken)
    {
        if (position.Next is null && position.WindowEnd - now < WindowKept)
            position = position with { Next = await OpenAsync(graph, calendarId, now, cancellationToken) };

        var link = position.Link;
        var rows = new Dictionary<string, Event>();

        while (true)
        {
            var page = await PageAsync(graph, calendarId, link, limit, cancellationToken)
                       ?? throw ChangesException.ProviderTransient();

            // The overview warns that an entity can appear more than once, and the latest row is the truth.
            foreach (var row in page.Value ?? [])
                rows[row.Id!] = row;

            var isLastPage = page.OdataDeltaLink is not null;
            if (isLastPage || changes.Count + rows.Count >= limit)
            {
                foreach (var row in rows.Values)
                    changes.Add(await ToChangeAsync(graph, calendarId, row, position.Since, cancellationToken));
            }

            if (page.OdataDeltaLink is { } done)
                return position.Next ?? position with { Link = done, Since = now, RoundDone = true };

            link = page.OdataNextLink ?? throw ChangesException.ProviderTransient();
            if (changes.Count >= limit)
                return position with { Link = link, RoundDone = false };
        }
    }

    // A row under @removed need not be a deletion: the reference says it also marks events outside
    // the window that were added or changed. Only the event itself can tell. So can a row with no
    // start, since the overview promises an update "at least the updated properties".
    private static async Task<Change> ToChangeAsync(
        GraphServiceClient graph, string calendarId, Event row, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var current = row;
        if (row.Start is null || row.AdditionalData?.ContainsKey(Removed) == true)
            current = await ReadEventAsync(graph, calendarId, row.Id!, cancellationToken);

        if (current is null)
            return new Change { Type = ChangeType.Deleted, Id = row.Id!, CalendarId = calendarId };

        return new Change
        {
            Type = current.CreatedDateTime >= since - EventChanges.NewEventTolerance ? ChangeType.Added : ChangeType.Updated,
            Id = row.Id!,
            CalendarId = calendarId,
            OccurredAt = current.LastModifiedDateTime,
            From = current.Organizer?.EmailAddress?.Address,
            Subject = current.Subject,
            Event = Facts(current)
        };
    }

    private static async Task<Event?> ReadEventAsync(
        GraphServiceClient graph, string calendarId, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await graph.Me.Calendars[calendarId].Events[id].GetAsync(config =>
            {
                config.QueryParameters.Select = EventFields;
                config.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
                GraphLinks.AskForLastingIds(config.Headers);
            }, cancellationToken);
        }
        catch (ApiException e) when (ProviderFailures.IsNotFound(e))
        {
            return null;
        }
    }

    private static EventFacts? Facts(Event item)
    {
        if (When(item.Start, item.IsAllDay) is not { } start)
            return null;

        return EventChanges.Facts(
            start,
            When(item.End, item.IsAllDay),
            item.IsCancelled == true ? EventChanges.Cancelled : EventChanges.Confirmed,
            item.IsOrganizer == true,
            MyResponse(item.ResponseStatus?.Response));
    }

    private static string? When(DateTimeTimeZone? time, bool? isAllDay)
    {
        if (time?.DateTime is not { } text)
            return null;

        var instant = DateTimeOffset.Parse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        return isAllDay == true ? EventChanges.Date(DateOnly.FromDateTime(instant.UtcDateTime)) : EventChanges.Instant(instant);
    }

    // The contract speaks Google's words, which is what Aura's rules compare with.
    private static string? MyResponse(ResponseType? response) => response switch
    {
        ResponseType.Accepted or ResponseType.Organizer => "accepted",
        ResponseType.Declined => "declined",
        ResponseType.TentativelyAccepted => "tentative",
        ResponseType.NotResponded or ResponseType.None => "needsAction",
        _ => null
    };

    private sealed record Position(string Link, Position? Next, DateTimeOffset Since, DateTimeOffset WindowEnd, bool RoundDone);
}
