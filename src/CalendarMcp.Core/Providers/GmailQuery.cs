using CalendarMcp.Core.Utilities;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Builds Gmail <c>q</c> date clauses. A date literal means midnight Pacific, so only epoch
/// seconds are exact: https://developers.google.com/workspace/gmail/api/guides/filtering
/// </summary>
internal static class GmailQuery
{
    public static string WithDateBounds(string query, DateTime? from, DateTime? to)
    {
        var terms = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(query))
            terms.Add(query);
        if (from is { } after)
            terms.Add($"after:{EpochSeconds(after)}");
        if (to is { } before)
            terms.Add($"before:{EpochSeconds(EndOfBound(before))}");
        return string.Join(' ', terms);
    }

    // search_emails documents the upper bound as "on or before this date", and a bare date
    // arrives as midnight, so that day is included rather than cut off at its first second.
    private static DateTime EndOfBound(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero ? value.AddDays(1) : value;

    private static long EpochSeconds(DateTime value) =>
        new DateTimeOffset(TimeZoneHelper.EnsureUtc(value)).ToUnixTimeSeconds();
}
