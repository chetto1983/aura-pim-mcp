using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Models;

namespace CalendarMcp.Core.Services;

/// <summary>
/// One provider's reading of one resource for <c>POST /admin/changes</c>. A source keeps nothing
/// between calls: the position it has reached travels in the cursor, which Aura stores.
/// </summary>
public interface IChangesSource
{
    /// <summary>The provider family, as <see cref="ChangesProviders.Family"/> names it.</summary>
    string Provider { get; }

    /// <summary><see cref="ChangeResource.Mail"/> or <see cref="ChangeResource.Calendar"/>.</summary>
    string Resource { get; }

    /// <summary>A cursor that stands at now, reading nothing that already exists.</summary>
    Task<ChangesCursor> StartAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// What changed after <paramref name="cursor"/>, at most about <paramref name="limit"/> entries.
    /// A provider's page is the unit a cursor can resume from, so the limit is exceeded rather than
    /// a change skipped. Throws <see cref="CursorExpiredException"/> when the position is gone.
    /// </summary>
    Task<ChangesBatch> ReadAsync(string accountId, ChangesCursor cursor, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Sender, subject and time of one item, reading no body, or null when it no longer exists.
    /// An item already reported by this source's <see cref="ReadAsync"/> must be readable by its id.
    /// </summary>
    Task<ItemFacts?> ReadItemAsync(string accountId, ItemRef item, CancellationToken cancellationToken);
}

/// <summary>What a source read, and the position that covers exactly that.</summary>
public sealed record ChangesBatch(IReadOnlyList<Change> Changes, ChangesCursor Next, bool HasMore)
{
    /// <summary>Mails in this batch left with no snippet because the page ran out of time. Not on the wire.</summary>
    public int SnippetsSkipped { get; init; }
}
