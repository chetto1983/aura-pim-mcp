namespace CalendarMcp.Core.Changes;

/// <summary>
/// The account's own side of one mail: has it written to this sender, or does the thread hold a
/// mail it sent. Lives for one request, so a burst from one sender costs one provider query.
/// A failed query fails the page: null would read as "cannot tell", and could file a person unseen.
/// </summary>
internal sealed class WrittenToLookup(
    Func<string, CancellationToken, Task<bool?>> sentToAddress,
    Func<string, CancellationToken, Task<bool?>> threadHoldsSent)
{
    private readonly Dictionary<string, bool?> _senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool?> _threads = new(StringComparer.Ordinal);

    /// <summary>
    /// The fact for one mail, or null when the provider cannot tell. The thread is asked only for a
    /// reply from someone the account has not written to, the one case where it can change the answer.
    /// </summary>
    public async Task<bool?> ForMailAsync(
        string? sender, bool isBulk, bool isReply, string? thread, CancellationToken cancellationToken)
    {
        if (isBulk || string.IsNullOrWhiteSpace(sender))
            return null;

        if (!_senders.TryGetValue(sender, out var written))
            _senders[sender] = written = await sentToAddress(sender, cancellationToken);

        if (written != false || !isReply || thread is null)
            return written;

        if (!_threads.TryGetValue(thread, out var inThread))
            _threads[thread] = inThread = await threadHoldsSent(thread, cancellationToken);

        return inThread;
    }
}
