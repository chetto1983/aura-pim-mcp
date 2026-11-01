using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Reads an account's calendars one by one, for a provider with no "all calendars" query. The
/// default calendar goes through its alias, so its events keep the <c>primary</c> id they have
/// always carried. One calendar failing keeps the others and is reported; every calendar failing
/// is an error, because an empty result would read as a free day.
/// </summary>
internal static class AllCalendars
{
    public static async Task<IEnumerable<CalendarEvent>> ReadAsync(
        IEnumerable<CalendarInfo> calendars,
        Func<string, Task<IEnumerable<CalendarEvent>>> read,
        int count)
    {
        var targets = calendars.ToList();
        if (targets.Count == 0)
            return await read(CalendarIdAliases.Primary);

        var events = new List<CalendarEvent>();
        var unread = new List<UnreadCalendar>();
        foreach (var calendar in targets)
        {
            try
            {
                events.AddRange(await read(calendar.IsDefault ? CalendarIdAliases.Primary : calendar.Id));
            }
            catch (Exception ex) when (ex is not (AccountAuthenticationRequiredException or OperationCanceledException))
            {
                unread.Add(new UnreadCalendar(calendar.Id, calendar.Name, ex));
            }
        }

        if (unread.Count == targets.Count)
            throw unread[0].Failure;

        var merged = events.OrderBy(e => e.Start).Take(count);
        return unread.Count == 0 ? merged.ToList() : new PartialCalendarRead(merged, unread);
    }
}
