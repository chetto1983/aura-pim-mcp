using Microsoft.Graph;

namespace CalendarMcp.Core.Services;

/// <summary>An authenticated Microsoft Graph client, for the code that reads what the provider interface does not expose.</summary>
public interface IGraphApiClients
{
    Task<GraphServiceClient> GraphAsync(string accountId, CancellationToken cancellationToken);
}
