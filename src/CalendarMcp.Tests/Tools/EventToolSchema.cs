using CalendarMcp.Tests.Helpers;

namespace CalendarMcp.Tests.Tools;

internal static class EventToolSchema
{
    /// <summary>The account an event tool acts on comes from its reference, so the published schema offers none.</summary>
    public static async Task AssertNoAccountId(string toolName)
    {
        var tool = (await SurfaceSession.ListToolsAsync()).Single(t => t.Name == toolName);
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties")
            .EnumerateObject().Select(p => p.Name).ToList();

        CollectionAssert.Contains(properties, "eventId");
        CollectionAssert.DoesNotContain(properties, "accountId");
    }
}
