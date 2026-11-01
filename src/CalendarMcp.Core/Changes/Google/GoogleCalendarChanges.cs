using System.Net;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using Google;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;

namespace CalendarMcp.Core.Changes.Google;

/// <summary>
/// Google Calendar through <c>events.list</c> with a sync token, one position per calendar
/// (https://developers.google.com/workspace/calendar/api/guides/sync). A token only exists on a
/// listing's last page, so a cursor stands either on a token or on a page of the listing it came from.
/// </summary>
internal sealed class GoogleCalendarChanges(IGoogleApiClients clients, TimeProvider clock) : IChangesSource
{
    // Page size is a parameter the sync guide wants unchanged between a listing and its token.
    // Holding it at the contract's largest limit keeps every token resumable under every limit.
    private const int PageSize = ChangesService.MaxLimit;

    private static readonly string Kind = ChangesCursor.KindOf(ChangesProviders.Google, ChangeResource.Calendar);

    public string Provider => ChangesProviders.Google;

    public string Resource => ChangeResource.Calendar;

    public async Task<ChangesCursor> StartAsync(string accountId, CancellationToken cancellationToken)
    {
        using var calendar = await clients.CalendarAsync(accountId, cancellationToken);
        var now = clock.GetUtcNow();

        var positions = new Dictionary<string, Position>();
        foreach (var id in await EditableCalendarsAsync(calendar, cancellationToken))
            positions[id] = await BaselineAsync(calendar, id, now, cancellationToken);

        return ChangesCursor.Of(Kind, positions);
    }

    public async Task<ChangesBatch> ReadAsync(
        string accountId, ChangesCursor cursor, int limit, CancellationToken cancellationToken)
    {
        var saved = cursor.Folders<Position>();

        using var calendar = await clients.CalendarAsync(accountId, cancellationToken);
        var now = clock.GetUtcNow();

        var positions = new Dictionary<string, Position>();
        var changes = new List<Change>();
        var hasMore = false;

        foreach (var id in await EditableCalendarsAsync(calendar, cancellationToken))
        {
            if (!saved.TryGetValue(id, out var position))
            {
                // A calendar the account just gained has nothing new in it yet, whatever it holds.
                positions[id] = await BaselineAsync(calendar, id, now, cancellationToken);
            }
            else if (changes.Count >= limit)
            {
                positions[id] = position;
                hasMore = true;
            }
            else
            {
                try
                {
                    positions[id] = await ReadCalendarAsync(calendar, id, position, limit, now, changes, cancellationToken);
                    hasMore |= positions[id].Page is not null;
                }
                catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Gone)
                {
                    throw new CursorExpiredException(ChangesCursor.Of(Kind, new Dictionary<string, Position>(saved)
                    {
                        [id] = await BaselineAsync(calendar, id, now, cancellationToken)
                    }));
                }
            }
        }

        return new ChangesBatch(changes, ChangesCursor.Of(Kind, positions), hasMore);
    }

    public async Task<ItemFacts?> ReadItemAsync(string accountId, ItemRef item, CancellationToken cancellationToken)
    {
        using var calendar = await clients.CalendarAsync(accountId, cancellationToken);

        try
        {
            var found = await calendar.Events.Get(item.CalendarId, item.Id).ExecuteAsync(cancellationToken);
            if (found.Status == EventChanges.Cancelled)
                return null;

            return new ItemFacts
            {
                Id = found.Id,
                CalendarId = item.CalendarId,
                OccurredAt = found.UpdatedDateTimeOffset,
                From = found.Organizer?.Email,
                Subject = found.Summary,
                Event = Facts(found)
            };
        }
        catch (GoogleApiException e) when (e.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }
    }

    // The account's role is read here rather than through minAccessRole, whose documentation gives
    // no order among roles. https://developers.google.com/workspace/calendar/api/v3/reference/calendarList
    private static async Task<List<string>> EditableCalendarsAsync(CalendarService calendar, CancellationToken cancellationToken)
    {
        var editable = new List<string>();
        var request = calendar.CalendarList.List();
        request.ShowHidden = true;

        do
        {
            var page = await request.ExecuteAsync(cancellationToken);
            editable.AddRange((page.Items ?? [])
                .Where(c => c.AccessRole is "owner" or "writer" or "writerWithoutPrivateAccess")
                .Select(c => c.Id));
            request.PageToken = page.NextPageToken;
        }
        while (request.PageToken is not null);

        return editable;
    }

    private static async Task<Position> BaselineAsync(
        CalendarService calendar, string id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var request = Listing(calendar, id);
        request.Fields = "nextPageToken,nextSyncToken";

        while (true)
        {
            var page = await request.ExecuteAsync(cancellationToken);
            if (page.NextPageToken is null)
                return new Position(page.NextSyncToken ?? throw ChangesException.ProviderTransient(), null, now);

            request.PageToken = page.NextPageToken;
        }
    }

    private static async Task<Position> ReadCalendarAsync(
        CalendarService calendar, string id, Position position, int limit, DateTimeOffset now,
        List<Change> changes, CancellationToken cancellationToken)
    {
        var request = Listing(calendar, id);
        request.SyncToken = position.Sync;
        request.PageToken = position.Page;

        while (true)
        {
            var page = await request.ExecuteAsync(cancellationToken);
            changes.AddRange((page.Items ?? []).Select(item => ToChange(id, item, position.Since)));

            if (page.NextPageToken is null)
                return new Position(page.NextSyncToken ?? throw ChangesException.ProviderTransient(), null, now);
            if (changes.Count >= limit)
                return position with { Page = page.NextPageToken };

            request.PageToken = page.NextPageToken;
        }
    }

    // Every parameter here must stay the same between a listing and the tokens it yields, or the
    // guide promises undefined behaviour. singleEvents gives a moved instance its own start.
    private static EventsResource.ListRequest Listing(CalendarService calendar, string id)
    {
        var request = calendar.Events.List(id);
        request.SingleEvents = true;
        request.ShowDeleted = true;
        request.MaxResults = PageSize;
        return request;
    }

    // A token returns the current event and not what happened to it, so "new" can only be read
    // from its creation time. An invitation to an older event looks like an update, and an update
    // can look new within the tolerance. Aura's verdict rows are what settle either.
    private static Change ToChange(string calendarId, Event item, DateTimeOffset since)
    {
        var type = item.Status == EventChanges.Cancelled
            ? ChangeType.Deleted
            : item.CreatedDateTimeOffset >= since - EventChanges.NewEventTolerance ? ChangeType.Added : ChangeType.Updated;

        return new Change
        {
            Type = type,
            Id = item.Id,
            CalendarId = calendarId,
            OccurredAt = item.UpdatedDateTimeOffset,
            From = item.Organizer?.Email,
            Subject = item.Summary,
            Event = Facts(item)
        };
    }

    // https://developers.google.com/workspace/calendar/api/v3/reference/events: a deleted event
    // guarantees only its id and a cancelled instance only id, recurringEventId and originalStartTime.
    private static EventFacts? Facts(Event item)
    {
        if ((When(item.Start) ?? When(item.OriginalStartTime)) is not { } start)
            return null;

        return EventChanges.Facts(
            start,
            When(item.End),
            item.Status == EventChanges.Cancelled ? EventChanges.Cancelled : EventChanges.Confirmed,
            item.Organizer?.Self == true,
            item.Attendees?.FirstOrDefault(a => a.Self == true)?.ResponseStatus);
    }

    private static string? When(EventDateTime? time) => time switch
    {
        { DateTimeDateTimeOffset: { } instant } => EventChanges.Instant(instant),
        { Date: { Length: > 0 } date } => date,
        _ => null
    };

    private sealed record Position(string Sync, string? Page, DateTimeOffset Since);
}
