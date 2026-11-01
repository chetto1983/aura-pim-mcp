using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarMcp.Core.Changes;

namespace CalendarMcp.Core.Models;

/// <summary>An instant as the changes contract writes it: UTC, whatever offset the provider gave.</summary>
internal sealed class UtcInstantConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EventChanges.Instant(value));
}
