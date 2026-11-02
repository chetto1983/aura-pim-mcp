using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Imap;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Utilities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using MimeKit.Utils;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// IMAP changes: UIDVALIDITY plus the last UID for new mail, CONDSTORE for updates where the server
/// has it, and a UID comparison for expunges. The folder is opened read-only, so nothing here can
/// mark a mail as read (RFC 9051 section 6.3.3, EXAMINE).
/// </summary>
public partial class ImapProviderService
{
    /// <summary>A body larger than this is not read for its snippet, which comes back empty.</summary>
    internal const uint MaxSnippetBodyOctets = MailBody.MaxBodyLength;

    /// <summary>
    /// What one page holds in memory for the conversions that follow it. A page can name 200 mails,
    /// and without this each could be a megabyte. A body past it is not read, and its snippet is empty.
    /// </summary>
    internal const long MaxHeldBodyOctets = 16 * MaxSnippetBodyOctets;

    /// <summary>Each part read is a round trip, and a mail can hold any number of blank ones.</summary>
    private const int MaxBodyFetches = 8;

    /// <summary>The newest sent mails checked by their recipient lists, since SEARCH only matches substrings.</summary>
    private const int MaxSentCandidates = 20;

    /// <summary>The last of a thread's references asked about: a reply quotes the ones nearest to it.</summary>
    private const int MaxThreadReferences = 10;

    private static readonly string ChangesKind = ChangesCursor.KindOf(ChangesProviders.Imap, ChangeResource.Mail);

    private static readonly MailParts.Shape<BodyPart> SummaryParts = new(
        part => part.ContentType.MimeType,
        part => part is BodyPartBasic basic && (basic.IsAttachment || basic.FileName is not null),
        part => part is BodyPartMultipart multipart ? multipart.BodyParts : []);

    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    public string Provider => ChangesProviders.Imap;

    public string Resource => ChangeResource.Mail;

    public async Task<ChangesCursor> StartAsync(string accountId, CancellationToken cancellationToken)
    {
        var cfg = await ResolveConfigAsync(accountId);

        return await WithImapAsync(cfg, async client =>
        {
            var folder = await OpenFolderAsync(client, cfg.InboxFolder, FolderAccess.ReadOnly, cancellationToken);
            var known = await NewestUidsAsync(folder, cancellationToken);
            var last = folder.UidNext is { } next ? next.Id - 1 : known.LastOrDefault();

            return At(cfg, new ImapPosition(folder.UidValidity, last, last, ModSeqOf(folder), ImapDelta.Compact(known)));
        }, cancellationToken);
    }

    public async Task<ChangesBatch> ReadAsync(
        string accountId, ChangesCursor cursor, int limit, CancellationToken cancellationToken)
    {
        var cfg = await ResolveConfigAsync(accountId);

        // The cursor was good when it was written. The account has been pointed at another folder since.
        if (!cursor.Has(cfg.InboxFolder))
            throw new CursorExpiredException();

        var saved = cursor.Folder<ImapPosition>(cfg.InboxFolder);
        var known = ImapDelta.Expand(saved.Known);

        // New mail is what follows this UID, and nothing can follow the last one there is.
        if (saved.LastUid == uint.MaxValue)
            throw ChangesException.InvalidCursor();

        var read = await WithImapAsync(cfg, async client =>
        {
            var folder = await OpenFolderAsync(client, cfg.InboxFolder, FolderAccess.ReadOnly, cancellationToken);
            if (folder.UidValidity != saved.UidValidity)
                throw new CursorExpiredException();

            // Read now: MailKit stops answering this once another folder is opened for written-to.
            var highest = ModSeqOf(folder);

            var vanished = await VanishedAsync(folder, known, cancellationToken);
            var waiting = await NewMailAsync(folder, saved.LastUid, cancellationToken);
            var taken = waiting.Take(limit).ToList();
            var added = await ReadAddedAsync(folder, cfg, taken, cancellationToken);

            var moreNewMail = waiting.Count > taken.Count;
            var updates = moreNewMail
                ? new ImapUpdates([], saved.ModSeq ?? 0, false)
                : await ReadUpdatesAsync(folder, saved, highest, Math.Max(1, limit - taken.Count), cancellationToken);

            var changes = vanished
                .Select(uid => new Change { Type = ChangeType.Deleted, Id = FormatEmailId(cfg.InboxFolder, saved.UidValidity, uid) })
                .ToList();
            changes.AddRange(await WithWrittenToAsync(client, cfg, added, cancellationToken));
            changes.AddRange(updates.Uids.Select(uid =>
                new Change { Type = ChangeType.Updated, Id = FormatEmailId(cfg.InboxFolder, saved.UidValidity, uid) }));

            var lastUid = taken.Count > 0 ? taken[^1] : saved.LastUid;
            var next = new ImapPosition(
                saved.UidValidity,
                lastUid,
                moreNewMail || updates.HasMore ? saved.BoundUid : lastUid,
                highest is null ? null : updates.NextModSeq,
                ImapDelta.Compact(ImapDelta.KnownAfter(known, vanished, taken)));

            return new ReadPage(changes, added, At(cfg, next), moreNewMail || updates.HasMore);
        }, cancellationToken);

        return WithSnippets(read, cancellationToken);
    }

    // After the gate: a page can take thirty seconds here, and every tool on the account waits at it.
    private ChangesBatch WithSnippets(ReadPage read, CancellationToken cancellationToken)
    {
        var snippets = new SnippetPage(Clock, cancellationToken);
        var bodies = read.Added.ToDictionary(mail => mail.Change.Id, mail => mail.Body);

        var changes = read.Changes
            .Select(change => bodies.GetValueOrDefault(change.Id) is { } body
                ? change with { Snippet = snippets.Of(() => (body.Body, body.Format)) }
                : change)
            .ToList();

        return new ChangesBatch(changes, read.Next, read.HasMore)
        {
            SnippetsSkipped = snippets.Skipped + read.Added.Count(mail => mail.LeftOut)
        };
    }

    public async Task<ItemFacts?> ReadItemAsync(string accountId, ItemRef item, CancellationToken cancellationToken)
    {
        if (!TryParseEmailId(item.Id!, out var folderName, out var uidValidity, out var uid))
            return null;
        if (uid == 0)
            throw ChangesException.InvalidRequest("invalid_items");

        var cfg = await ResolveConfigAsync(accountId);

        return await WithImapAsync(cfg, async client =>
        {
            IMailFolder folder;
            try
            {
                folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadOnly, cancellationToken);
            }
            catch (FolderNotFoundException)
            {
                return null;
            }

            // Another UIDVALIDITY means this UID may name a different mail now.
            if (folder.UidValidity != uidValidity)
                return null;

            var summaries = await folder.FetchAsync(
                [new UniqueId(uid)],
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate,
                cancellationToken);
            if (summaries.SingleOrDefault() is not { } summary)
                return null;

            var (from, fromName) = MailSender.ReadEnvelope(summary.Envelope?.From.Mailboxes.FirstOrDefault());
            return new ItemFacts
            {
                Id = item.Id!,
                OccurredAt = summary.InternalDate ?? summary.Envelope?.Date,
                From = from,
                FromName = fromName,
                Subject = summary.Envelope?.Subject
            };
        }, cancellationToken);
    }

    private static bool TryParseEmailId(string id, out string folder, out uint uidValidity, out uint uid)
    {
        try
        {
            (folder, uidValidity, uid) = ParseEmailId(id);
            return true;
        }
        catch (ProviderOperationException)
        {
            (folder, uidValidity, uid) = (string.Empty, 0, 0);
            return false;
        }
    }

    private static ChangesCursor At(ImapAccountConfig cfg, ImapPosition position) =>
        ChangesCursor.Of(ChangesKind, cfg.InboxFolder, position);

    private static ulong? ModSeqOf(IMailFolder folder) =>
        folder.Supports(FolderFeature.ModSequences) ? folder.HighestModSeq : null;

    private static async Task<IReadOnlyList<uint>> NewestUidsAsync(IMailFolder folder, CancellationToken cancellationToken)
    {
        if (folder.Count == 0)
            return [];

        var first = Math.Max(0, folder.Count - ImapDelta.KnownWindow);
        var summaries = await folder.FetchAsync(first, -1, MessageSummaryItems.UniqueId, cancellationToken);

        return summaries.Select(s => s.UniqueId.Id).Order().ToList();
    }

    private static async Task<IReadOnlyList<uint>> VanishedAsync(
        IMailFolder folder, IReadOnlyList<uint> known, CancellationToken cancellationToken)
    {
        if (known.Count == 0)
            return [];

        var uids = known.Select(uid => new UniqueId(uid)).ToList();
        var present = await folder.SearchAsync(SearchQuery.Uids(uids), cancellationToken);

        return ImapDelta.Vanished(known, present.Select(uid => uid.Id));
    }

    private static async Task<IReadOnlyList<uint>> NewMailAsync(IMailFolder folder, uint lastUid, CancellationToken cancellationToken)
    {
        var found = await folder.SearchAsync(
            SearchQuery.Uids(new UniqueIdRange(new UniqueId(lastUid + 1), UniqueId.MaxValue)), cancellationToken);

        return ImapDelta.NewSince(lastUid, found.Select(uid => uid.Id));
    }

    // A mail newer than BoundUid has a mod-sequence of its own from arriving: reading it here would
    // report every new mail as updated too. Every mod-sequence is read with UIDs up to that bound.
    private static async Task<ImapUpdates> ReadUpdatesAsync(
        IMailFolder folder, ImapPosition saved, ulong? highestModSeq, int limit, CancellationToken cancellationToken)
    {
        if (highestModSeq is not { } highest)
            return new ImapUpdates([], 0, false);
        if (saved.ModSeq is not { } modSeq || saved.BoundUid == 0)
            return new ImapUpdates([], highest, false);

        var scope = new UniqueIdRange(UniqueId.MinValue, new UniqueId(saved.BoundUid));
        var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.ModSeq) { ChangedSince = modSeq };
        var changed = await folder.FetchAsync(scope, request, cancellationToken);

        return ImapDelta.CutUpdates(changed.Select(s => (s.UniqueId.Id, s.ModSeq ?? modSeq)), limit, highest);
    }

    private async Task<List<AddedMail>> ReadAddedAsync(
        IMailFolder folder, ImapAccountConfig cfg, IReadOnlyList<uint> uids, CancellationToken cancellationToken)
    {
        if (uids.Count == 0)
            return [];

        // A mail expunged since the search is simply not in the answer: nobody saw it.
        var ids = uids.Select(uid => new UniqueId(uid)).ToList();
        var summaries = await folder.FetchAsync(ids, EnvelopeItems, cancellationToken);

        var added = new List<AddedMail>();
        var held = 0L;
        foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
        {
            var (body, leftOut) = await ReadBodyAsync(folder, summary, MaxHeldBodyOctets - held, cancellationToken);
            held += body?.Octets ?? 0;

            added.Add(ToAdded(cfg, folder.UidValidity, summary, body, leftOut));
        }

        return added;
    }

    private static AddedMail ToAdded(ImapAccountConfig cfg, uint uidValidity, IMessageSummary summary, MailText? body, bool leftOut)
    {
        // HeaderList's indexer answers the first match, which on a delivered mail is the topmost.
        var header = (string name) => summary.Headers?[name];

        var (from, fromName) = MailSender.ReadEnvelope(summary.Envelope?.From.Mailboxes.FirstOrDefault());
        var bulk = MailHeaderSignals.IsBulk(header);
        var references = ThreadReferences(header);
        var thread = references.FirstOrDefault() ?? summary.Envelope?.MessageId;

        var change = new Change
        {
            Type = ChangeType.Added,
            Id = FormatEmailId(cfg.InboxFolder, uidValidity, summary.UniqueId.Id),
            OccurredAt = summary.InternalDate ?? summary.Envelope?.Date,
            From = from,
            FromName = fromName,
            Subject = summary.Envelope?.Subject,
            Snippet = string.Empty,
            Signals = new MailSignals
            {
                ProviderImportant = MailHeaderSignals.IsImportant(header),
                Bulk = bulk,
                AuthFailed = MailHeaderSignals.AuthenticationFailed(header("Authentication-Results")),
                ThreadId = thread,
                Invitation = summary.Body is { } tree
                    && MailParts.Any(tree, SummaryParts, part => MailHeaderSignals.IsScheduling(part.ContentType))
            }
        };

        return new AddedMail(change, from, bulk, references, body, leftOut);
    }

    private static List<string> ThreadReferences(Func<string, string?> header)
    {
        var references = new List<string>();
        foreach (var value in new[] { header("References"), header("In-Reply-To") })
        {
            if (value is not null)
                references.AddRange(MimeUtils.EnumerateReferences(value));
        }

        return references.Distinct().ToList();
    }

    /// <summary>
    /// The first part of the mail that holds anything, in the order of <see cref="MailParts"/>. Over
    /// the cap there is no snippet, and a body with no room left on the page is left out of it.
    /// </summary>
    private static async Task<(MailText? Body, bool LeftOut)> ReadBodyAsync(
        IMailFolder folder, IMessageSummary summary, long room, CancellationToken cancellationToken)
    {
        if (summary.Body is null)
            return (null, false);

        var candidates = MailParts.BodyCandidates(summary.Body, SummaryParts).OfType<BodyPartText>();
        foreach (var part in candidates.Take(MaxBodyFetches))
        {
            if (part.Octets > MaxSnippetBodyOctets)
                return (null, false);
            if (part.Octets > room)
                return (null, true);
            if (part.Octets == 0)
                continue;

            var text = await ReadPartAsync(folder, summary, part, cancellationToken);
            if (!MailParts.IsBlank(text))
                return (new MailText(text, MailParts.FormatOf(part.ContentType.MimeType), part.Octets), false);
        }

        return (null, false);
    }

    // GetBodyPartAsync would decode this for free, but it asks for a part and its MIME headers in one
    // FETCH and reads an empty body from a server that answers them the other way round, as GreenMail
    // does and as RFC 9051 allows. The part's description is already in hand from BODYSTRUCTURE.
    private static async Task<string> ReadPartAsync(
        IMailFolder folder, IMessageSummary summary, BodyPartText part, CancellationToken cancellationToken)
    {
        // A single-part mail's body has no specifier of its own, and an empty section is the whole message.
        var section = part.PartSpecifier.Length == 0 ? "TEXT" : part.PartSpecifier;
        using var content = await folder.GetStreamAsync(summary.UniqueId, section, cancellationToken);

        try
        {
            MimeUtils.TryParse(part.ContentTransferEncoding, out ContentEncoding encoding);
            var body = new TextPart(part.ContentType.MediaSubtype) { Content = new MimeContent(content, encoding) };
            body.ContentType.Charset = part.ContentType.Charset;

            return body.Text;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    // Sent is opened only now: a connection has one selected folder, and this ends the read of the Inbox.
    private async Task<IEnumerable<Change>> WithWrittenToAsync(
        ImapClient client, ImapAccountConfig cfg, List<AddedMail> added, CancellationToken cancellationToken)
    {
        if (!added.Any(mail => mail is { Bulk: false, Sender: not null }))
            return added.Select(mail => mail.Change);

        var sent = await OpenSentAsync(client, cfg, cancellationToken);
        var writtenTo = new WrittenToLookup(
            (address, ct) => SentToAsync(sent, address, ct),
            (references, ct) => ThreadHoldsSentAsync(sent, references.Split(' '), ct));

        var changes = new List<Change>();
        foreach (var mail in added)
        {
            var fact = await writtenTo.ForMailAsync(
                mail.Sender, mail.Bulk, mail.References.Count > 0,
                mail.References.Count > 0 ? string.Join(' ', mail.References.TakeLast(MaxThreadReferences)) : null,
                cancellationToken);

            changes.Add(mail.Change with { Signals = mail.Change.Signals! with { WrittenTo = fact } });
        }

        return changes;
    }

    /// <summary>The Sent folder, or null when the account has none: then written-to cannot be told.</summary>
    private static async Task<IMailFolder?> OpenSentAsync(ImapClient client, ImapAccountConfig cfg, CancellationToken cancellationToken)
    {
        IMailFolder? sent;
        try
        {
            sent = await client.GetFolderAsync(cfg.SentFolder, cancellationToken);
        }
        catch (FolderNotFoundException)
        {
            sent = GetSpecialFolder(client, SpecialFolder.Sent);
        }

        if (sent is null)
            return null;

        var codes = ImapNoCodes.Of(client);
        codes?.Reset();

        // MailKit keeps a folder it has listed, so one deleted since is found again and refused at EXAMINE.
        try
        {
            await sent.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        }
        catch (FolderNotFoundException)
        {
            return null;
        }
        catch (ImapCommandException e) when (e.Response == ImapCommandResponse.No)
        {
            // A NO's wording is the server's own, and it also means later (RFC 5530: UNAVAILABLE, INUSE).
            // Read as gone, a blip would lose the page its written-to, and the cursor would pass those mails.
            // The kinds no waiting changes (\Noselect, NOPERM, CANNOT) would fail the page for good: the cursor
            // never moves, and the first ordinary mail would stop the account's watch. Those read as gone too.
            var permanent = sent.Attributes.HasFlag(FolderAttributes.NoSelect) || codes?.SawPermanent == true;
            if (!permanent && await IsStillListedAsync(sent, cancellationToken))
                throw;

            return null;
        }

        return sent;
    }

    // GetSubfolderAsync answers from MailKit's cache, which is the reason to ask. This one sends a LIST.
    private static async Task<bool> IsStillListedAsync(IMailFolder folder, CancellationToken cancellationToken)
    {
        if (folder.ParentFolder is not { } parent)
            return true;

        var listed = await parent.GetSubfoldersAsync(false, cancellationToken);
        return listed.Any(candidate => candidate.FullName == folder.FullName);
    }

    private static async Task<bool?> SentToAsync(IMailFolder? sent, string address, CancellationToken cancellationToken)
    {
        if (sent is null || !MailSender.IsPlainAddress(address))
            return null;

        var query = SearchQuery.ToContains(address).Or(SearchQuery.CcContains(address)).Or(SearchQuery.BccContains(address));
        var found = await sent.SearchAsync(query, cancellationToken);
        if (found.Count == 0)
            return false;

        var candidates = found.TakeLast(MaxSentCandidates).ToList();
        var summaries = await sent.FetchAsync(candidates, MessageSummaryItems.Envelope, cancellationToken);

        return Verdict(found.Count, summaries.Any(summary => Recipients(summary.Envelope)
            .Any(mailbox => string.Equals(mailbox.Address, address, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Only the newest hits are compared, so past the cap a miss proves nothing about the rest.</summary>
    private static bool? Verdict(int hits, bool matched) => matched ? true : hits > MaxSentCandidates ? null : false;

    private static IEnumerable<MailboxAddress> Recipients(Envelope? envelope) => envelope is null
        ? []
        : envelope.To.Mailboxes.Concat(envelope.Cc.Mailboxes).Concat(envelope.Bcc.Mailboxes);

    private static async Task<bool?> ThreadHoldsSentAsync(
        IMailFolder? sent, IEnumerable<string> references, CancellationToken cancellationToken)
    {
        if (sent is null)
            return null;

        var ids = references.Where(IsMessageId).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            return false;

        var query = ids
            .Select(id => (SearchQuery)SearchQuery.HeaderContains("Message-Id", id))
            .Aggregate((left, right) => left.Or(right));

        // SEARCH HEADER matches a substring, and References is the sender's to write, so a hit proves
        // nothing until the id itself is compared. RFC 9051 section 6.4.4.
        var found = await sent.SearchAsync(query, cancellationToken);
        if (found.Count == 0)
            return false;

        var candidates = found.TakeLast(MaxSentCandidates).ToList();
        var summaries = await sent.FetchAsync(candidates, MessageSummaryItems.Envelope, cancellationToken);

        return Verdict(found.Count, summaries.Any(summary => summary.Envelope?.MessageId is { } id && ids.Contains(id)));
    }

    private static bool IsMessageId(string reference)
    {
        var at = reference.IndexOf('@');
        return at > 0 && at < reference.Length - 1;
    }

    private sealed record MailText(string Body, string Format, uint Octets);

    private sealed record AddedMail(
        Change Change, string? Sender, bool Bulk, IReadOnlyList<string> References, MailText? Body, bool LeftOut);

    private sealed record ReadPage(List<Change> Changes, List<AddedMail> Added, ChangesCursor Next, bool HasMore);
}
