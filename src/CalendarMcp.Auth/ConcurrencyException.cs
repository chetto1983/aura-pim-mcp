namespace CalendarMcp.Auth;

/// <summary>The account changed on disk after the caller read it, so the caller's write would undo that change.</summary>
public sealed class ConcurrencyException(string accountId)
    : Exception($"Account '{accountId}' changed after it was read.");
