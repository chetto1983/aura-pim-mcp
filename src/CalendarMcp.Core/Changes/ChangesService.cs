using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Core.Changes;

/// <summary>
/// The rules of <c>POST /admin/changes</c> that hold for every provider: whose account it is, what
/// the account may do, what an empty or expired cursor means and how a failure is named. A source
/// only reads. The tenant comes from <see cref="IAccountRegistry"/>, which filters by the bearer.
/// </summary>
public sealed class ChangesService(
    IAccountRegistry accounts,
    IEnumerable<IChangesSource> sources,
    ILogger<ChangesService> logger)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxItems = 50;

    public async Task<ChangesPage> ReadAsync(
        string? accountId, string? resource, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var (account, source) = await ResolveAsync(accountId, resource);
        var kind = ChangesCursor.KindOf(source.Provider, source.Resource);

        return await GuardAsync(account.Id, resource!, async () =>
        {
            if (string.IsNullOrEmpty(cursor))
            {
                var start = await source.StartAsync(account.Id, cancellationToken);
                return new ChangesPage([], start.Encode(), false, false);
            }

            try
            {
                var batch = await source.ReadAsync(
                    account.Id, ChangesCursor.Decode(cursor, kind), EffectiveLimit(limit), cancellationToken);
                return new ChangesPage(batch.Changes, batch.Next.Encode(), batch.HasMore, false);
            }
            catch (CursorExpiredException expired)
            {
                logger.LogWarning("Cursor expired for {Resource} on account {AccountId}", resource, account.Id);
                var fresh = expired.Replacement ?? await source.StartAsync(account.Id, cancellationToken);
                return new ChangesPage([], fresh.Encode(), false, true);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Reads items by id for the digest and the decisions view. An item that is gone is an answer
    /// and not a failure, so a mail deleted since the morning does not cost the digest. A provider
    /// failure still fails the call: "unreadable" must not read as "gone".
    /// </summary>
    public async Task<ItemsPage> ReadItemsAsync(
        string? accountId, string? resource, IReadOnlyList<ItemRef>? items, CancellationToken cancellationToken)
    {
        if (items is null)
            throw ChangesException.InvalidRequest("invalid_items");
        if (items.Count > MaxItems)
            throw ChangesException.InvalidRequest("too_many_items");

        var (account, source) = await ResolveAsync(accountId, resource);
        if (items.Any(item => string.IsNullOrEmpty(item.Id) || (resource == ChangeResource.Calendar && string.IsNullOrEmpty(item.CalendarId))))
            throw ChangesException.InvalidRequest("invalid_items");

        return await GuardAsync(account.Id, resource!, async () =>
        {
            var found = new List<ItemFacts>();
            var gone = new List<ItemRef>();

            foreach (var item in items)
            {
                if (await source.ReadItemAsync(account.Id, item, cancellationToken) is { } facts)
                    found.Add(facts);
                else
                    gone.Add(item);
            }

            return new ItemsPage(found, gone);
        }, cancellationToken);
    }

    private async Task<(AccountInfo Account, IChangesSource Source)> ResolveAsync(string? accountId, string? resource)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw ChangesException.InvalidRequest("invalid_account");
        if (resource is not (ChangeResource.Mail or ChangeResource.Calendar))
            throw ChangesException.InvalidRequest("invalid_resource");

        // Another tenant's account and one that does not exist answer alike, so an id cannot be probed.
        var account = await accounts.GetAccountAsync(accountId) ?? throw ChangesException.AccountNotFound();

        var family = ChangesProviders.Family(account.Provider);
        var source = sources.FirstOrDefault(s => s.Provider == family && s.Resource == resource)
                     ?? throw ChangesException.Unsupported();

        var permission = resource == ChangeResource.Mail ? AccountPermission.EmailRead : AccountPermission.CalendarRead;
        if (!AccountCapabilities.IsAllowed(account, permission))
            throw ChangesException.PermissionDenied();

        return (account, source);
    }

    private static int EffectiveLimit(int? limit) => limit is null or <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    // Logs the exception's type and not its message: a provider's message can quote the request,
    // and a request can hold a delta link.
    private async Task<T> GuardAsync<T>(
        string accountId, string resource, Func<Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            return await read();
        }
        catch (Exception e) when (ProviderFailures.Classify(e, cancellationToken) is { } failure)
        {
            logger.LogWarning(
                "Reading {Resource} on account {AccountId} failed as {Error} ({ExceptionType})",
                resource, accountId, failure.Error, e.GetType().Name);
            throw failure;
        }
    }
}
