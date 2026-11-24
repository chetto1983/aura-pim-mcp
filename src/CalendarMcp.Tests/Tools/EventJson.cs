using CalendarMcp.Core.Tools;

namespace CalendarMcp.Tests.Tools;

internal static class EventJson
{
    /// <summary>The provider's own id, recovered from the reference an event carries.</summary>
    public static string RawId(this System.Text.Json.JsonElement evt)
    {
        Assert.IsTrue(EventRef.TryDecode(evt.GetProperty("eventId").GetString(), out _, out var rawId));
        return rawId;
    }
}
