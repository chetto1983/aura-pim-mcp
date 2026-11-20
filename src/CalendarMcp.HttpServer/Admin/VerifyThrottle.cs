namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// One verification per account every 30 seconds, whoever asks: a wrong password is several login attempts on the
/// mailbox, and providers lock an account that collects too many (spec amendment A5). Aura limits each identity on
/// its side; this is the limit a caller cannot spread across identities.
/// </summary>
public sealed class VerifyThrottle(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>True, and the account's window starts now, unless a check of it started less than 30 s ago.</summary>
    public bool TryEnter(string accountId)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            // A closed window holds nothing, and ids that were tried once must not pile up.
            foreach (var closed in _started.Where(entry => now - entry.Value >= Window).Select(entry => entry.Key).ToList())
                _started.Remove(closed);

            if (_started.ContainsKey(accountId))
                return false;

            _started[accountId] = now;
            return true;
        }
    }
}
