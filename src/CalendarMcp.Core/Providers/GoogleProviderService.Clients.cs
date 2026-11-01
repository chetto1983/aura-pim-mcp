using CalendarMcp.Core.Services;
using Google.Apis.Calendar.v3;
using Google.Apis.Gmail.v1;

namespace CalendarMcp.Core.Providers;

public partial class GoogleProviderService : IGoogleApiClients
{
    public async Task<GmailService> GmailAsync(string accountId, CancellationToken cancellationToken) =>
        CreateGmailService(await GetCredentialAsync(accountId, cancellationToken));

    public async Task<CalendarService> CalendarAsync(string accountId, CancellationToken cancellationToken) =>
        CreateCalendarService(await GetCredentialAsync(accountId, cancellationToken));
}
