using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Utilities;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using ChangedMessages = Microsoft.Graph.Me.MailFolders.Item.Messages.Delta.DeltaGetResponse;
using ChangeType = CalendarMcp.Core.Models.ChangeType;

namespace CalendarMcp.Core.Changes.Graph;

/// <summary>
/// Microsoft mail through message delta on the Inbox (https://learn.microsoft.com/en-us/graph/delta-query-messages).
/// A delta row does not say what happened to a message, so each kind of change is its own stream
/// with a <c>changeType</c>: that is what makes <c>added</c> real rather than a guess from a date.
/// </summary>
/// <remarks>
/// A stream is opened with <c>$filter=receivedDateTime ge</c> so its first round is empty rather than
/// the whole Inbox three times. The page documents both options but never shows them together, so this
/// pairing is unproven until a real mailbox runs it. A refusal would be loud: an error on the first call.
/// </remarks>
internal sealed class GraphMailChanges(string provider, IGraphApiClients clients, TimeProvider clock) : IChangesSource
{
    private const string Inbox = "inbox";
    private const string SentItems = "sentitems";
    private const string Created = "created";
    private const string Updated = "updated";
    private const string Deleted = "deleted";

    /// <summary>
    /// "Applying $filter in a delta query returns only up to 5,000 messages", and the page does not say
    /// per round or for the stream's life. If it is for life a stream would go quietly blind, so
    /// each one is replaced well before any mailbox could reach that number.
    /// </summary>
    private static readonly TimeSpan StreamLife = TimeSpan.FromDays(1);

    /// <summary>A mail can show up after the time it is stamped with. A new stream reaches back by this much.</summary>
    private static readonly TimeSpan DeliveryLag = TimeSpan.FromHours(1);

    private static readonly string[] Fields =
    [
        "id", "subject", "from", "receivedDateTime", "body", "importance",
        "inferenceClassification", "conversationId", "internetMessageHeaders"
    ];

    private string Kind => ChangesCursor.KindOf(provider, ChangeResource.Mail);

    public string Provider => provider;

    public string Resource => ChangeResource.Mail;

    public async Task<ChangesCursor> StartAsync(string accountId, CancellationToken cancellationToken)
    {
        var graph = await clients.GraphAsync(accountId, cancellationToken);
        var now = clock.GetUtcNow();

        return At(new Streams(
            await OpenAsync(graph, Deleted, now, cancellationToken),
            await OpenAsync(graph, Created, now, cancellationToken),
            await OpenAsync(graph, Updated, now, cancellationToken)));
    }

    public async Task<ChangesBatch> ReadAsync(
        string accountId, ChangesCursor cursor, int limit, CancellationToken cancellationToken)
    {
        var saved = cursor.Folder<Streams>(Inbox);
        var links = new[] { saved.Deleted, saved.Created, saved.Updated };
        foreach (var stream in links)
        {
            GraphLinks.Checked(stream.Link);
            if (stream.Successor is not null)
                GraphLinks.Checked(stream.Successor);
        }

        var graph = await clients.GraphAsync(accountId, cancellationToken);
        var now = clock.GetUtcNow();
        var rows = new DeltaRows();

        try
        {
            var deleted = await AdvanceAsync(graph, Deleted, saved.Deleted, limit, now, rows, cancellationToken);
            var created = await AdvanceAsync(graph, Created, saved.Created, limit, now, rows, cancellationToken);
            var updated = await AdvanceAsync(graph, Updated, saved.Updated, limit, now, rows, cancellationToken);

            var changes = await ToChangesAsync(graph, rows, cancellationToken);
            var hasMore = !deleted.RoundDone || !created.RoundDone || !updated.RoundDone;

            return new ChangesBatch(changes, At(new Streams(deleted, created, updated)), hasMore);
        }
        catch (ApiException e) when (GraphLinks.IsExpired(e))
        {
            throw new CursorExpiredException();
        }
    }

    public async Task<ItemFacts?> ReadItemAsync(string accountId, ItemRef item, CancellationToken cancellationToken)
    {
        var graph = await clients.GraphAsync(accountId, cancellationToken);

        try
        {
            var message = await graph.Me.Messages[item.Id].GetAsync(
                config => config.QueryParameters.Select = ["id", "subject", "from", "receivedDateTime"], cancellationToken);
            if (message is null)
                return null;

            return new ItemFacts
            {
                Id = message.Id!,
                OccurredAt = message.ReceivedDateTime,
                From = message.From?.EmailAddress?.Address,
                FromName = message.From?.EmailAddress?.Name,
                Subject = message.Subject
            };
        }
        catch (ApiException e) when (ProviderFailures.IsNotFound(e))
        {
            return null;
        }
    }

    private ChangesCursor At(Streams streams) => ChangesCursor.Of(Kind, Inbox, streams);

    private static async Task<Stream> OpenAsync(
        GraphServiceClient graph, string changeType, DateTimeOffset now, CancellationToken cancellationToken) =>
        new(await OpenLinkAsync(graph, changeType, now, cancellationToken), null, now, true);

    private static async Task<string> OpenLinkAsync(
        GraphServiceClient graph, string changeType, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var page = await graph.Me.MailFolders[Inbox].Messages.Delta.GetAsDeltaGetResponseAsync(config =>
        {
            config.QueryParameters.ChangeType = changeType;
            config.QueryParameters.Filter = $"receivedDateTime ge {EventChanges.Instant(now - DeliveryLag)}";
            config.QueryParameters.Select = ["id"];
            config.Headers.Add("Prefer", $"odata.maxpagesize={ChangesService.MaxLimit}");
        }, cancellationToken);

        while (page?.OdataDeltaLink is null)
        {
            var next = page?.OdataNextLink ?? throw ChangesException.ProviderTransient();
            page = await PageAsync(graph, next, ChangesService.MaxLimit, cancellationToken);
        }

        return page.OdataDeltaLink;
    }

    private static Task<ChangedMessages?> PageAsync(
        GraphServiceClient graph, string link, int pageSize, CancellationToken cancellationToken) =>
        graph.Me.MailFolders[Inbox].Messages.Delta.WithUrl(GraphLinks.Checked(link)).GetAsDeltaGetResponseAsync(
            config => config.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}"), cancellationToken);

    /// <summary>
    /// Walks a stream until its round ends or the page is full. A stream past its life gets its
    /// successor before the last read of the old one, so the two overlap instead of leaving a gap:
    /// a mail in the overlap can be reported twice, which Aura's verdict rows absorb.
    /// </summary>
    private static async Task<Stream> AdvanceAsync(
        GraphServiceClient graph, string changeType, Stream stream, int limit, DateTimeOffset now,
        DeltaRows rows, CancellationToken cancellationToken)
    {
        if (rows.Count >= limit)
            return stream with { RoundDone = false };

        if (stream.Successor is null && now - stream.OpenedAt >= StreamLife)
            stream = stream with { Successor = await OpenLinkAsync(graph, changeType, now, cancellationToken) };

        var link = stream.Link;
        while (true)
        {
            var page = await PageAsync(graph, link, limit, cancellationToken)
                       ?? throw ChangesException.ProviderTransient();
            rows.Add(changeType, page.Value ?? []);

            if (page.OdataDeltaLink is { } done)
            {
                return stream.Successor is { } successor
                    ? new Stream(successor, null, now, true)
                    : stream with { Link = done, RoundDone = true };
            }

            link = page.OdataNextLink ?? throw ChangesException.ProviderTransient();
            if (rows.Count >= limit)
                return stream with { Link = link, RoundDone = false };
        }
    }

    private static async Task<List<Change>> ToChangesAsync(
        GraphServiceClient graph, DeltaRows rows, CancellationToken cancellationToken)
    {
        var writtenTo = new WrittenToLookup(
            (address, ct) => SentToAsync(graph, address, ct),
            (conversation, ct) => ThreadHoldsSentAsync(graph, conversation, ct));

        var changes = new List<Change>();
        foreach (var (id, type) in rows.Net())
        {
            if (type != ChangeType.Added)
            {
                changes.Add(new Change { Type = type, Id = id });
                continue;
            }

            var message = await ReadMessageAsync(graph, id, cancellationToken);
            if (message is not null)
                changes.Add(await AddedAsync(message, writtenTo, cancellationToken));
        }

        return changes;
    }

    private static async Task<Message?> ReadMessageAsync(GraphServiceClient graph, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await graph.Me.Messages[id].GetAsync(
                config => config.QueryParameters.Select = Fields, cancellationToken);
        }
        catch (ApiException e) when (ProviderFailures.IsNotFound(e))
        {
            return null;
        }
    }

    private static async Task<Change> AddedAsync(Message message, WrittenToLookup writtenTo, CancellationToken cancellationToken)
    {
        var header = (string name) => message.InternetMessageHeaders?
            .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        var address = message.From?.EmailAddress?.Address;
        var bulk = MailHeaderSignals.IsBulk(header);
        var format = message.Body?.ContentType == BodyType.Html ? MailBody.HtmlFormat : "text";

        return new Change
        {
            Type = ChangeType.Added,
            Id = message.Id!,
            OccurredAt = message.ReceivedDateTime,
            From = address,
            FromName = message.From?.EmailAddress?.Name,
            Subject = message.Subject,
            Snippet = MailSnippet.Of(message.Body?.Content ?? string.Empty, format),
            Signals = new MailSignals
            {
                ProviderImportant = message.Importance == Importance.High,
                ProviderCategory = message.InferenceClassification?.ToString().ToLowerInvariant(),
                Bulk = bulk,
                AuthFailed = MailHeaderSignals.AuthenticationFailed(header("Authentication-Results")),
                ThreadId = message.ConversationId,
                WrittenTo = await writtenTo.ForMailAsync(
                    address, bulk, MailHeaderSignals.IsReply(header), message.ConversationId, cancellationToken)
            }
        };
    }

    // https://learn.microsoft.com/en-us/graph/search-query-parameter: recipients covers to, cc and
    // bcc. A sender is attacker text, so an address that would need escaping is not searched at all.
    private static async Task<bool?> SentToAsync(GraphServiceClient graph, string address, CancellationToken cancellationToken)
    {
        if (!MailSender.IsPlainAddress(address))
            return null;

        var found = await graph.Me.MailFolders[SentItems].Messages.GetAsync(config =>
        {
            config.QueryParameters.Search = $"\"recipients:{address}\"";
            config.QueryParameters.Top = 1;
            config.QueryParameters.Select = ["id"];
        }, cancellationToken);

        return found?.Value is { Count: > 0 };
    }

    private static async Task<bool?> ThreadHoldsSentAsync(GraphServiceClient graph, string conversation, CancellationToken cancellationToken)
    {
        var found = await graph.Me.MailFolders[SentItems].Messages.GetAsync(config =>
        {
            config.QueryParameters.Filter = $"conversationId eq '{conversation.Replace("'", "''", StringComparison.Ordinal)}'";
            config.QueryParameters.Top = 1;
            config.QueryParameters.Select = ["id"];
        }, cancellationToken);

        return found?.Value is { Count: > 0 };
    }

    private sealed record Stream(string Link, string? Successor, DateTimeOffset OpenedAt, bool RoundDone);

    private sealed record Streams(Stream Deleted, Stream Created, Stream Updated);

    /// <summary>The rows of one read, netted: an id can come through several streams and several pages.</summary>
    private sealed class DeltaRows
    {
        private readonly List<(string Id, string Type)> _rows = [];

        public int Count => _rows.Count;

        public void Add(string changeType, IEnumerable<Message> messages)
        {
            foreach (var message in messages)
            {
                // A row saying it was removed is one whatever the stream: the page warns that some
                // events ignore the request's filters.
                var removed = message.AdditionalData?.ContainsKey("@removed") == true;
                var type = removed || changeType == Deleted
                    ? ChangeType.Deleted
                    : changeType == Created ? ChangeType.Added : ChangeType.Updated;

                _rows.Add((message.Id!, type));
            }
        }

        public IEnumerable<(string Id, string Type)> Net()
        {
            var deleted = _rows.Where(r => r.Type == ChangeType.Deleted).Select(r => r.Id).ToHashSet();
            var added = _rows.Where(r => r.Type == ChangeType.Added).Select(r => r.Id).ToHashSet();

            var kept = new HashSet<string>();
            foreach (var (id, type) in _rows)
            {
                var superseded = (type == ChangeType.Updated && (added.Contains(id) || deleted.Contains(id)))
                                 || (added.Contains(id) && deleted.Contains(id));
                if (!superseded && kept.Add(id))
                    yield return (id, type);
            }
        }
    }
}
