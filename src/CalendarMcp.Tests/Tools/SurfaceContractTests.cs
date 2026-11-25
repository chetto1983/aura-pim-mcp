using CalendarMcp.Core.Apps;
using CalendarMcp.Tests.Helpers;
using ModelContextProtocol.Client;

namespace CalendarMcp.Tests.Tools;

/// <summary>
/// What a client sees in tools/list: one tool per operation, under the names Aura's stored rows use,
/// each with its own required fields, and the view bound to the reads it renders.
/// </summary>
[TestClass]
public sealed class SurfaceContractTests
{
    public static readonly string[] ToolNames =
    [
        "list_accounts", "get_emails", "get_email_details", "search_emails", "list_calendars",
        "get_calendar_events", "get_calendar_event_details", "get_contacts", "search_contacts",
        "get_contact_details", "create_event", "update_event", "respond_to_event", "send_email",
        "delete_email", "mark_email_read", "move_email", "delete_event", "create_contact",
        "update_contact", "delete_contact", "get_email_attachment", "get_contextual_email_summary",
        "get_guide", "get_unsubscribe_info", "unsubscribe_from_email", "bulk_delete_emails",
        "bulk_mark_emails_read", "bulk_move_emails",
    ];

    private static readonly string[] ViewTools =
    [
        "list_accounts", "get_emails", "get_email_details", "search_emails", "list_calendars",
        "get_calendar_events", "get_calendar_event_details", "get_contacts", "search_contacts",
        "get_contact_details",
    ];

    [TestMethod]
    public async Task ToolsList_AdvertisesExactlyTheTwentyNineOperations()
    {
        var tools = await SurfaceSession.ListToolsAsync();

        Assert.HasCount(29, ToolNames);
        CollectionAssert.AreEquivalent(ToolNames, tools.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task NoSchema_HasAnActionArgument()
    {
        foreach (var tool in await SurfaceSession.ListToolsAsync())
            Assert.IsFalse(Properties(tool).Contains("action"), $"{tool.Name} still takes an action.");
    }

    private static readonly Dictionary<string, string[]> Required = new()
    {
        ["list_accounts"] = [],
        ["get_emails"] = [],
        ["get_email_details"] = ["accountId", "emailId"],
        ["search_emails"] = ["query"],
        ["list_calendars"] = [],
        ["get_calendar_events"] = ["timeZone"],
        ["get_calendar_event_details"] = ["timeZone", "calendarId", "eventId"],
        ["get_contacts"] = [],
        ["search_contacts"] = ["query"],
        ["get_contact_details"] = ["accountId", "contactId"],
        ["create_event"] = ["subject", "start", "end", "accountId"],
        ["update_event"] = ["calendarId", "eventId"],
        ["respond_to_event"] = ["eventId", "response"],
        ["send_email"] = ["accountId", "to", "subject"],
        ["delete_email"] = ["accountId", "emailId"],
        ["mark_email_read"] = ["accountId", "emailId", "isRead"],
        ["move_email"] = ["accountId", "emailId", "destination"],
        ["delete_event"] = ["eventId"],
        ["create_contact"] = ["displayName", "accountId"],
        ["update_contact"] = ["accountId", "contactId"],
        ["delete_contact"] = ["accountId", "contactId"],
        ["get_email_attachment"] = ["accountId", "emailId", "attachmentId"],
        ["get_contextual_email_summary"] = [],
        ["get_guide"] = [],
        ["get_unsubscribe_info"] = ["accountId", "emailId"],
        ["unsubscribe_from_email"] = ["accountId", "emailId"],
        ["bulk_delete_emails"] = ["items"],
        ["bulk_mark_emails_read"] = ["items", "isRead"],
        ["bulk_move_emails"] = ["items", "destination"],
    };

    public static IEnumerable<object[]> RequiredRows => Required.Select(pair => new object[] { pair.Key, pair.Value });

    [TestMethod]
    public void Required_NamesEveryTool() =>
        CollectionAssert.AreEquivalent(ToolNames, Required.Keys.ToArray());

    [TestMethod]
    [DynamicData(nameof(RequiredRows))]
    public async Task Required_IsPinnedPerTool(string name, string[] required)
    {
        var tool = (await SurfaceSession.ListToolsAsync()).Single(t => t.Name == name);

        var published = tool.ProtocolTool.InputSchema.TryGetProperty("required", out var list)
            ? list.EnumerateArray().Select(e => e.GetString()).ToArray()
            : [];
        CollectionAssert.AreEquivalent(required, published, name);
    }

    [TestMethod]
    public async Task ExactlyTheReadsTheViewRenders_CarryTheView()
    {
        var bound = new List<string>();
        foreach (var tool in await SurfaceSession.ListToolsAsync())
        {
            if (tool.ProtocolTool.Meta?["ui"] is not { } ui)
                continue;
            Assert.AreEqual(CalendarView.ResourceUri, ui["resourceUri"]?.GetValue<string>(), tool.Name);
            bound.Add(tool.Name);
        }

        CollectionAssert.AreEquivalent(ViewTools, bound);
    }

    private static HashSet<string> Properties(McpClientTool tool) =>
        tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet()
            : [];
}
