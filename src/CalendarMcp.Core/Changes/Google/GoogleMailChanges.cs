using System.Globalization;
using System.Net;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Util;

namespace CalendarMcp.Core.Changes.Google;

/// <summary>
/// Gmail through <c>users.history.list</c>
/// (https://developers.google.com/workspace/gmail/api/guides/sync). The filter on <c>INBOX</c> is
/// left off the request and applied here, because a filtered history cannot show a mail leaving the Inbox.
/// </summary>
internal sealed class GoogleMailChanges(IGoogleApiClients clients, TimeProvider? clock = null) : IChangesSource
{
    private const string Me = "me";
    private const string Sent = "SENT";
    private const string Important = "IMPORTANT";
    private const string CategoryPrefix = "CATEGORY_";

    private static readonly string Kind = ChangesCursor.KindOf(ChangesProviders.Google, ChangeResource.Mail);

    public string Provider => ChangesProviders.Google;

    public string Resource => ChangeResource.Mail;

    public async Task<ChangesCursor> StartAsync(string accountId, CancellationToken cancellationToken)
    {
        using var gmail = await clients.GmailAsync(accountId, cancellationToken);
        var profile = await gmail.Users.GetProfile(Me).ExecuteAsync(cancellationToken);

        return At(profile.HistoryId ?? throw ChangesException.ProviderTransient());
    }

    public async Task<ChangesBatch> ReadAsync(
        string accountId, ChangesCursor cursor, int limit, CancellationToken cancellationToken)
    {
        var from = cursor.Folder<Position>(GmailHistory.Inbox).History;

        using var gmail = await clients.GmailAsync(accountId, cancellationToken);
        var page = await ReadHistoryAsync(gmail, from, limit, cancellationToken);
        var records = page.History ?? [];

        var writtenTo = new WrittenToLookup(
            (address, ct) => SentToAsync(gmail, address, ct),
            (thread, ct) => ThreadHoldsSentAsync(gmail, thread, ct));
        var snippets = new SnippetPage(clock ?? TimeProvider.System, cancellationToken);

        var changes = new List<Change>();
        foreach (var (id, type) in GmailHistory.Net(records))
        {
            if (type != ChangeType.Added)
            {
                changes.Add(new Change { Type = type, Id = id });
                continue;
            }

            // A mail can be gone by the time it is read. The sync guide does not cover this; a 404
            // from messages.get is the only signal, and a mail nobody saw needs no deletion.
            var message = await ReadMessageAsync(gmail, id, cancellationToken);
            if (message is not null)
                changes.Add(await AddedAsync(message, writtenTo, snippets, cancellationToken));
        }

        var truncated = page.NextPageToken is not null;
        var next = truncated ? records[^1].Id : page.HistoryId;

        return new ChangesBatch(changes, next is null ? cursor : At(next.Value), truncated)
        {
            SnippetsSkipped = snippets.Skipped
        };
    }

    // The metadata format is documented as id, labels and headers, so internalDate may not come with it.
    public async Task<ItemFacts?> ReadItemAsync(string accountId, ItemRef item, CancellationToken cancellationToken)
    {
        using var gmail = await clients.GmailAsync(accountId, cancellationToken);

        var request = gmail.Users.Messages.Get(Me, item.Id);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
        request.MetadataHeaders = new Repeatable<string>(["From", "Subject", "Date"]);

        try
        {
            var message = await request.ExecuteAsync(cancellationToken);
            var header = (string name) => message.Payload?.Headers?
                .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
            var (address, name) = MailSender.Read(header("From"));

            return new ItemFacts
            {
                Id = message.Id,
                OccurredAt = message.InternalDate is { } date
                    ? DateTimeOffset.FromUnixTimeMilliseconds(date)
                    : MailSender.ReadDate(header("Date")),
                From = address,
                FromName = name,
                Subject = header("Subject")
            };
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static ChangesCursor At(ulong history) =>
        ChangesCursor.Of(Kind, GmailHistory.Inbox, new Position(history));

    // A cut page resumes after its last record, so a page token never reaches the cursor. An empty
    // page holding a token has no record to resume from, which only the next page can resolve.
    private static async Task<ListHistoryResponse> ReadHistoryAsync(
        GmailService gmail, ulong from, int limit, CancellationToken cancellationToken)
    {
        var request = gmail.Users.History.List(Me);
        request.StartHistoryId = from;
        request.MaxResults = limit;
        request.HistoryTypesList = new Repeatable<UsersResource.HistoryResource.ListRequest.HistoryTypesEnum>(
        [
            UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageAdded,
            UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageDeleted,
            UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.LabelAdded,
            UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.LabelRemoved
        ]);

        try
        {
            while (true)
            {
                var page = await request.ExecuteAsync(cancellationToken);
                if (page.History is { Count: > 0 } || page.NextPageToken is null)
                    return page;

                request.PageToken = page.NextPageToken;
            }
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw new CursorExpiredException();
        }
    }

    private static async Task<Message?> ReadMessageAsync(GmailService gmail, string id, CancellationToken cancellationToken)
    {
        try
        {
            return await gmail.Users.Messages.Get(Me, id).ExecuteAsync(cancellationToken);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<Change> AddedAsync(
        Message message, WrittenToLookup writtenTo, SnippetPage snippets, CancellationToken cancellationToken)
    {
        // Gmail hands headers over in their order in the message, which keeps the topmost first.
        var header = (string name) => message.Payload?.Headers?
            .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        var labels = message.LabelIds ?? [];
        var (address, name) = MailSender.Read(header("From"));
        var bulk = MailHeaderSignals.IsBulk(header);

        return new Change
        {
            Type = ChangeType.Added,
            Id = message.Id,
            OccurredAt = message.InternalDate is { } date ? DateTimeOffset.FromUnixTimeMilliseconds(date) : null,
            From = address,
            FromName = name,
            Subject = header("Subject"),
            Snippet = snippets.Of(() => GoogleProviderService.GetMessageBody(message)),
            Signals = new MailSignals
            {
                ProviderImportant = labels.Contains(Important),
                ProviderCategory = labels.FirstOrDefault(l => l.StartsWith(CategoryPrefix, StringComparison.Ordinal))?
                    [CategoryPrefix.Length..].ToLowerInvariant(),
                Bulk = bulk,
                AuthFailed = MailHeaderSignals.AuthenticationFailed(header("Authentication-Results")),
                ThreadId = message.ThreadId,
                WrittenTo = await writtenTo.ForMailAsync(
                    address, bulk, MailHeaderSignals.IsReply(header), message.ThreadId, cancellationToken)
            }
        };
    }

    // The search box's own syntax: https://support.google.com/mail/answer/7190. A sender is attacker
    // text, so an address that needs quoting is not put into a query at all.
    private static async Task<bool?> SentToAsync(GmailService gmail, string address, CancellationToken cancellationToken)
    {
        if (!MailSender.IsPlainAddress(address))
            return null;

        var request = gmail.Users.Messages.List(Me);
        request.LabelIds = Sent;
        request.Q = string.Format(CultureInfo.InvariantCulture, "to:{0} OR cc:{0} OR bcc:{0}", address);
        request.MaxResults = 1;

        var found = await request.ExecuteAsync(cancellationToken);
        return found.Messages is { Count: > 0 };
    }

    private static async Task<bool?> ThreadHoldsSentAsync(GmailService gmail, string thread, CancellationToken cancellationToken)
    {
        var request = gmail.Users.Threads.Get(Me, thread);
        request.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Minimal;

        try
        {
            var found = await request.ExecuteAsync(cancellationToken);
            return found.Messages?.Any(m => m.LabelIds?.Contains(Sent) == true) ?? false;
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private sealed record Position(ulong History);
}
