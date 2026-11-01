using System.Globalization;

namespace CalendarMcp.Core.Changes.Imap;

/// <summary>A mailbox position: only what IMAP can name, kept in a cursor because the PIM keeps nothing.</summary>
/// <param name="UidValidity">The UID space this position belongs to.</param>
/// <param name="LastUid">The highest UID already reported or older than the cursor.</param>
/// <param name="BoundUid">The highest UID <paramref name="ModSeq"/> covers. A newer mail's first mod-sequence is not an update.</param>
/// <param name="ModSeq">The mod-sequence already reported, on a server that has them.</param>
/// <param name="Known">The newest UIDs seen, as a compact set, which is how an expunge is noticed.</param>
internal sealed record ImapPosition(uint UidValidity, uint LastUid, uint BoundUid, ulong? ModSeq, string Known);

/// <summary>Updated mail cut to a page, with the mod-sequence that page covers.</summary>
internal sealed record ImapUpdates(IReadOnlyList<uint> Uids, ulong NextModSeq, bool HasMore);

/// <summary>
/// The arithmetic of an IMAP position, apart from any server. CONDSTORE does not report expunges
/// (RFC 7162 section 3.1), and QRESYNC would be a second code path the test server cannot exercise,
/// so a deletion is noticed by comparing the newest UIDs with what the server still has.
/// </summary>
internal static class ImapDelta
{
    /// <summary>
    /// How many of the newest UIDs a cursor remembers. A deletion further back goes unnoticed, and a
    /// cursor stays small: Aura stores one per account and sends it back every tick.
    /// </summary>
    public const int KnownWindow = 1000;

    /// <summary>The UIDs as ranges: <c>1:5,7</c>. A mailbox's newest UIDs are mostly contiguous.</summary>
    public static string Compact(IEnumerable<uint> uids)
    {
        var parts = new List<string>();
        uint? first = null, last = null;

        foreach (var uid in uids.Distinct().Order())
        {
            if (first is not null && uid == last + 1)
            {
                last = uid;
                continue;
            }

            if (first is not null)
                parts.Add(Range(first.Value, last!.Value));
            first = last = uid;
        }

        if (first is not null)
            parts.Add(Range(first.Value, last!.Value));

        return string.Join(',', parts);
    }

    private static string Range(uint first, uint last) => first == last
        ? first.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{first}:{last}");

    /// <summary>
    /// The UIDs of a compact set. The set comes from an unsigned cursor, so its size is checked
    /// before anything is expanded: <c>1:4294967295</c> must not become four billion numbers.
    /// A UID starts at 1, and MailKit's <see cref="MailKit.UniqueId"/> throws on 0.
    /// </summary>
    public static IReadOnlyList<uint> Expand(string compact)
    {
        var uids = new List<uint>();
        if (compact.Length == 0)
            return uids;

        foreach (var part in compact.Split(','))
        {
            var ends = part.Split(':');
            if (ends.Length > 2 || !uint.TryParse(ends[0], CultureInfo.InvariantCulture, out var first))
                throw ChangesException.InvalidCursor();

            var last = first;
            if (ends.Length == 2 && !uint.TryParse(ends[1], CultureInfo.InvariantCulture, out last))
                throw ChangesException.InvalidCursor();
            if (first == 0 || last < first || last - first >= KnownWindow || uids.Count + (last - first) >= KnownWindow)
                throw ChangesException.InvalidCursor();

            for (var offset = 0u; offset <= last - first; offset++)
                uids.Add(first + offset);
        }

        return uids;
    }

    /// <summary>What the cursor knew and the server no longer has.</summary>
    public static IReadOnlyList<uint> Vanished(IEnumerable<uint> known, IEnumerable<uint> present)
    {
        var there = present.ToHashSet();
        return known.Where(uid => !there.Contains(uid)).ToList();
    }

    /// <summary>
    /// New mail, oldest first. <c>n:*</c> always matches the last message even when <c>n</c> is past
    /// every UID (RFC 9051 section 6.4.8), so a mailbox with nothing new still answers its newest mail.
    /// </summary>
    public static IReadOnlyList<uint> NewSince(uint lastUid, IEnumerable<uint> found) =>
        found.Where(uid => uid > lastUid).Order().ToList();

    /// <summary>The set a cursor carries on: what survived and what arrived, newest kept.</summary>
    public static IReadOnlyList<uint> KnownAfter(IEnumerable<uint> known, IEnumerable<uint> vanished, IEnumerable<uint> added) =>
        known.Except(vanished).Concat(added).Distinct().Order().TakeLast(KnownWindow).ToList();

    /// <summary>
    /// Cuts updated mail to a page in mod-sequence order. Several messages can share a mod-sequence
    /// (RFC 7162 section 3.1.3) and a cursor can only stand between two values, so a group is never
    /// split: the page runs over the limit rather than lose the rest of the group.
    /// </summary>
    public static ImapUpdates CutUpdates(IEnumerable<(uint Uid, ulong ModSeq)> updates, int limit, ulong highest)
    {
        var ordered = updates.OrderBy(u => u.ModSeq).ThenBy(u => u.Uid).ToList();
        if (ordered.Count <= limit)
            return new ImapUpdates(ordered.Select(u => u.Uid).ToList(), highest, false);

        var lastTaken = ordered[limit - 1].ModSeq;
        var taken = ordered.TakeWhile(u => u.ModSeq <= lastTaken).ToList();

        return new ImapUpdates(taken.Select(u => u.Uid).ToList(), lastTaken, taken.Count < ordered.Count);
    }
}
