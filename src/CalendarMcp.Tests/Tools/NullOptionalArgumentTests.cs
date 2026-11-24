using System.Text.Json;
using CalendarMcp.Tests.Helpers;

namespace CalendarMcp.Tests.Tools;

/// <summary>
/// A model sends <c>null</c> for an optional argument it means to leave out (measured: <c>unreadOnly: null</c>).
/// Each optional number or bool is nullable and takes its default in the body, so null and absent
/// reach the tool alike instead of failing binding.
/// </summary>
[TestClass]
public sealed class NullOptionalArgumentTests
{
    private const string A = SurfaceSession.AccountA;
    private const string Span = """ "start":"2026-10-01T09:00:00Z","end":"2026-10-01T10:00:00Z" """;

    [TestMethod]
    [DataRow("get_emails", $$"""{"accountId":"{{A}}","count":null}""")]
    [DataRow("get_emails", $$"""{"accountId":"{{A}}","unreadOnly":null}""")]
    [DataRow("search_emails", $$"""{"query":"x","accountId":"{{A}}","count":null}""")]
    [DataRow("get_calendar_events", $$"""{"timeZone":"UTC","accountId":"{{A}}","count":null}""")]
    [DataRow("get_calendar_events", $$"""{"timeZone":"UTC","accountId":"{{A}}","includeAttendeeDetails":null}""")]
    [DataRow("get_contacts", $$"""{"accountId":"{{A}}","count":null}""")]
    [DataRow("search_contacts", $$"""{"query":"x","accountId":"{{A}}","count":null}""")]
    [DataRow("create_event", $$"""{"subject":"s",{{Span}},"accountId":"{{A}}","isAllDay":null}""")]
    [DataRow("get_contextual_email_summary", """{"countPerAccount":null}""")]
    [DataRow("get_contextual_email_summary", """{"unreadOnly":null}""")]
    [DataRow("get_contextual_email_summary", """{"includeBodyPreview":null}""")]
    [DataRow("get_contextual_email_summary", """{"maxSamplesPerCluster":null}""")]
    [DataRow("send_email", $$"""{"accountId":"{{A}}","to":["x@example.org"],"subject":"s","body":null,"bodyFormat":null}""")]
    [DataRow("unsubscribe_from_email", $$"""{"accountId":"{{A}}","emailId":"e","method":null}""")]
    public async Task ANullOptionalArgument_ReachesTheTool(string tool, string arguments)
    {
        var factory = new RecordingProviderFactory();

        var result = await SurfaceSession.CallAsync(factory, tool, arguments);

        Assert.AreNotEqual($"An error occurred invoking '{tool}'.", SurfaceSession.Text(result));
        CollectionAssert.Contains(factory.Requested.ToArray(), SurfaceSession.ProviderA, SurfaceSession.Text(result));
    }

    [TestMethod]
    public async Task EveryOptionalNumberOrBool_AcceptsNull()
    {
        var refusing = new List<string>();
        foreach (var tool in await SurfaceSession.ListToolsAsync())
        {
            var schema = tool.ProtocolTool.InputSchema;
            var required = schema.TryGetProperty("required", out var list)
                ? list.EnumerateArray().Select(e => e.GetString()).ToHashSet()
                : [];
            foreach (var property in schema.GetProperty("properties").EnumerateObject())
            {
                var types = Types(property.Value);
                if (!required.Contains(property.Name)
                    && types.Overlaps(["integer", "number", "boolean"]) && !types.Contains("null"))
                    refusing.Add($"{tool.Name}.{property.Name}");
            }
        }

        Assert.IsEmpty(refusing, string.Join(", ", refusing));
    }

    private static HashSet<string> Types(JsonElement property) =>
        !property.TryGetProperty("type", out var type) ? []
        : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToHashSet()
        : [type.GetString()!];
}
