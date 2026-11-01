using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalendarMcp.Core.Models;

namespace CalendarMcp.Core.Changes;

/// <summary>
/// How an event is written into a change, the same for every provider. A provider's cursor returns
/// the current event and not what changed, so the fingerprint is how Aura tells a moved event from
/// a new description or someone else's reply.
/// </summary>
internal static class EventChanges
{
    public const string Confirmed = "confirmed";
    public const string Cancelled = "cancelled";

    /// <summary>The window a provider's clock may disagree by when telling a new event from an old one.</summary>
    public static readonly TimeSpan NewEventTolerance = TimeSpan.FromMinutes(2);

    public static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static EventFacts Facts(string start, string? end, string status, bool organizerIsSelf, string? myResponse) => new()
    {
        Start = start,
        End = end,
        Status = status,
        OrganizerIsSelf = organizerIsSelf,
        MyResponse = myResponse,
        Fingerprint = Fingerprint(start, end, status)
    };

    /// <summary>A hash of start, end and status: exactly what makes a change worth telling a person.</summary>
    public static string Fingerprint(string start, string? end, string status) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{start}|{end}|{status}")));
}
