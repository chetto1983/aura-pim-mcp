using CalendarMcp.Core.Models;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// Internal argument bag for the action dispatcher. The MCP-facing method keeps its flat,
/// schema-friendly signature; this type gives the routing table one testable input.
/// </summary>
internal sealed class CalendarActionArguments
{
    public string? AccountId { get; init; }
    public string? CalendarId { get; init; }
    public string? EventId { get; init; }
    public string? EmailId { get; init; }
    public string? ContactId { get; init; }
    public string? Query { get; init; }
    public int? Count { get; init; }
    public bool? UnreadOnly { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public List<string>? To { get; init; }
    public string? Subject { get; init; }
    public string? Body { get; init; }
    public string? BodyFormat { get; init; }
    public List<string>? Cc { get; init; }
    public List<OutboundEmailAttachment>? Attachments { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public string? TimeZone { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public string? Location { get; init; }
    public List<string>? Attendees { get; init; }
    public string? Response { get; init; }
    public string? Comment { get; init; }
    public bool? IsRead { get; init; }
    public string? Destination { get; init; }
    public string? DisplayName { get; init; }
    public string? GivenName { get; init; }
    public string? Surname { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? JobTitle { get; init; }
    public string? CompanyName { get; init; }
    public string? Notes { get; init; }
    public string? AttachmentId { get; init; }
    public string? Mode { get; init; }
    public string? Topics { get; init; }
    public int? CountPerAccount { get; init; }
    public bool? IncludeBodyPreview { get; init; }
    public int? MaxSamplesPerCluster { get; init; }
    public string? Name { get; init; }
    public string? Method { get; init; }
    public BulkEmailItem[]? Items { get; init; }
}
