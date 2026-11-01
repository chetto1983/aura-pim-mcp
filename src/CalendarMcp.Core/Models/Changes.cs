using System.Text.Json.Serialization;

namespace CalendarMcp.Core.Models;

/// <summary>What <c>POST /admin/changes</c> reads: <c>mail</c> or <c>calendar</c>.</summary>
public static class ChangeResource
{
    public const string Mail = "mail";
    public const string Calendar = "calendar";
}

/// <summary>What happened to a mail or an event since the cursor.</summary>
public static class ChangeType
{
    public const string Added = "added";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
}

/// <summary>
/// What the provider knows about a new mail, for rules that run with no model.
/// <see cref="WrittenTo"/> stays in the JSON when null: null is a fact, "this provider cannot tell".
/// </summary>
public sealed record MailSignals
{
    public bool ProviderImportant { get; init; }
    public string? ProviderCategory { get; init; }
    public bool Bulk { get; init; }
    public bool AuthFailed { get; init; }
    public string? ThreadId { get; init; }
    public bool? WrittenTo { get; init; }
}

/// <summary>The current event. <see cref="Fingerprint"/> tells a moved event from a new description.</summary>
public sealed record EventFacts
{
    public required string Start { get; init; }
    public string? End { get; init; }
    public required string Status { get; init; }
    public bool OrganizerIsSelf { get; init; }
    public string? MyResponse { get; init; }
    public required string Fingerprint { get; init; }
}

/// <summary>One entry of a changes page. Only what the type carries is written out.</summary>
public sealed record Change
{
    public required string Type { get; init; }
    public required string Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CalendarId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? OccurredAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Snippet { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MailSignals? Signals { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventFacts? Event { get; init; }
}

/// <summary>The answer of <c>POST /admin/changes</c>.</summary>
public sealed record ChangesPage(
    IReadOnlyList<Change> Changes,
    string NextCursor,
    bool HasMore,
    bool ResetRequired);
