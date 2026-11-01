using Google.Apis.Calendar.v3;
using Google.Apis.Gmail.v1;

namespace CalendarMcp.Core.Services;

/// <summary>Authenticated Google clients, for the code that reads what the provider interface does not expose.</summary>
public interface IGoogleApiClients
{
    Task<GmailService> GmailAsync(string accountId, CancellationToken cancellationToken);

    Task<CalendarService> CalendarAsync(string accountId, CancellationToken cancellationToken);
}
