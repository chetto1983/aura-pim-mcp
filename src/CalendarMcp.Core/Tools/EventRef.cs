using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// Opaque calendar-event reference minted by <c>get_calendar_events</c> and <c>create_event</c> and
/// consumed by <c>get_calendar_event_details</c>, <c>update_event</c>, <c>delete_event</c> and
/// <c>respond_to_event</c> (MCP-05, D-20). Encodes
/// the account id alongside the provider's own event id so those tools
/// never ask the caller for an <c>accountId</c> -- the account is resolved
/// server-side from the reference itself, and a missing or malformed
/// reference is rejected outright rather than silently resolved against a
/// default account.
/// </summary>
/// <remarks>
/// The encoding is an implementation detail private to this server: callers
/// must treat the string as opaque and pass it back
/// byte-for-byte, exactly the discipline the design doc's <c>eventId</c>
/// contract requires. There is no numeric or precision surface in this path
/// -- it is a string round-trip, nothing more.
/// </remarks>
internal static class EventRef
{
    private sealed class Payload
    {
        [JsonPropertyName("a")]
        public string AccountId { get; set; } = "";

        [JsonPropertyName("e")]
        public string EventId { get; set; } = "";
    }

    /// <summary>
    /// Encodes an account id and the provider's own event id into a single
    /// opaque reference string.
    /// </summary>
    public static string Encode(string accountId, string eventId)
    {
        var json = JsonSerializer.Serialize(new Payload { AccountId = accountId, EventId = eventId });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Attempts to decode a reference previously produced by <see cref="Encode"/>.
    /// Returns <see langword="false"/> -- never throws -- for a missing,
    /// malformed, or foreign-looking reference, so the caller can reject it
    /// as a plain validation failure instead of resolving against a default
    /// account.
    /// </summary>
    public static bool TryDecode(string? reference, out string accountId, out string eventId)
    {
        accountId = "";
        eventId = "";

        if (string.IsNullOrEmpty(reference))
            return false;

        try
        {
            var bytes = Convert.FromBase64String(reference);
            var payload = JsonSerializer.Deserialize<Payload>(bytes);
            if (payload is null || string.IsNullOrEmpty(payload.AccountId) || string.IsNullOrEmpty(payload.EventId))
                return false;

            accountId = payload.AccountId;
            eventId = payload.EventId;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// The account and provider id a caller's reference wraps. A missing or malformed reference
    /// is rejected, never resolved against a default account.
    /// </summary>
    public static (string AccountId, string EventId) Resolve(string? reference)
    {
        ToolGuard.RequireNonEmpty(reference, "eventId");
        if (!TryDecode(reference, out var accountId, out var eventId))
        {
            throw new McpException(
                "eventId is not a valid event reference. Obtain it from the eventId field returned by get_calendar_events or create_event -- do not construct or guess one.");
        }

        return (accountId, eventId);
    }

    /// <summary>An error that quotes the provider id quotes the reference instead: the caller only holds that.</summary>
    public static McpException Requote(McpException error, string reference, string eventId) =>
        error.Message.Contains(eventId, StringComparison.Ordinal)
            ? new(error.Message.Replace(eventId, reference, StringComparison.Ordinal), error)
            : error;
}
