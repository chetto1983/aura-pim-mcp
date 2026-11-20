using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarMcp.Core.Discovery;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// <c>POST /admin/discover</c>: where a mailbox lives, from its address, for Aura's connect screen (account
/// connect/repair design, A6). It reads nothing of the tenant's and stores nothing; only the address's domain, or a
/// suffix of its MX host, leaves the box.
/// </summary>
public static class DiscoverEndpoint
{
    private static readonly JsonSerializerOptions Answer = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapDiscoverEndpoint(this RouteGroupBuilder admin)
    {
        admin.MapPost("/discover", DiscoverAsync);
        return admin;
    }

    /// <summary><c>{"email": "…"}</c>.</summary>
    public sealed record DiscoverRequest(string? Email);

    private static async Task<IResult> DiscoverAsync(DiscoverRequest? body, MailDiscovery discovery, CancellationToken ct)
    {
        if (body?.Email is not { } email || MailDiscovery.Normalise(email) is null)
            return VerifyResults.Refused("invalid_email");

        return Results.Json(await discovery.DiscoverAsync(email, ct), Answer);
    }
}
