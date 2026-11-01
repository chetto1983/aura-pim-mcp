namespace CalendarMcp.Core.Models;

/// <summary>A calendar an all-calendars read could not reach, and why.</summary>
public sealed record UnreadCalendar(string CalendarId, string CalendarName, Exception Failure);

/// <summary>
/// The events of an all-calendars read in which some calendars failed. Returned in place of a
/// plain list so that the tool can warn about them instead of showing a partial day as complete.
/// </summary>
public sealed class PartialCalendarRead(IEnumerable<CalendarEvent> events, IReadOnlyList<UnreadCalendar> unread)
    : List<CalendarEvent>(events)
{
    public IReadOnlyList<UnreadCalendar> Unread { get; } = unread;
}
