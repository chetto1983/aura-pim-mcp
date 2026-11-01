using CalendarMcp.Core.Services;
using Microsoft.Graph;

namespace CalendarMcp.Core.Providers;

public partial class OutlookComProviderService : IGraphApiClients
{
    public async Task<GraphServiceClient> GraphAsync(string accountId, CancellationToken cancellationToken) =>
        CreateGraphClient(new BearerTokenAuthenticationProvider(await GetAccessTokenAsync(accountId, cancellationToken)));
}
