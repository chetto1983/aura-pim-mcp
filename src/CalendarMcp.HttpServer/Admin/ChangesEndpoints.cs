using CalendarMcp.Core.Changes;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// The contract Aura's watch reads. Not an MCP action: the model never sees it. It is a POST because
/// a cursor can hold one delta link per calendar, which would overflow a request line and land in
/// access logs. <see cref="AdminAuthMiddleware"/> gives it the bearer and the tenant.
/// </summary>
public static class ChangesEndpoints
{
    public sealed record ChangesRequest(string? AccountId, string? Resource, string? Cursor, int? Limit);

    public static RouteGroupBuilder MapChangesEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapPost("/changes", ReadChanges);
        return admin;
    }

    private static Task<IResult> ReadChanges(
        ChangesRequest request, ChangesService changes, CancellationToken cancellationToken) =>
        AnswerAsync(async () => Results.Ok(await changes.ReadAsync(
            request.AccountId, request.Resource, request.Cursor, request.Limit, cancellationToken)));

    private static async Task<IResult> AnswerAsync(Func<Task<IResult>> read)
    {
        try
        {
            return await read();
        }
        catch (ChangesException failure)
        {
            return Results.Json(new { error = failure.Error }, statusCode: failure.StatusCode);
        }
    }
}
