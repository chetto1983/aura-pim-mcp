using CalendarMcp.Core.Tools;
using CalendarMcp.Tests.Helpers;
using CalendarMcp.Tests.Tools;

namespace CalendarMcp.Tests.Tenancy;

/// <summary>
/// Every advertised tool, called as tenant A while tenant B's account is configured, either
/// refuses B's account as unknown or answers from A's alone. B's account is the only one on the
/// Google provider, so a call that reached it would show in the providers the call resolved.
/// </summary>
[TestClass]
public sealed class ToolSurfaceTenancyTests
{
    private const string B = SurfaceSession.AccountB;
    private static readonly string ReferenceB = EventRef.Encode(B, "b-event");
    private static readonly string NotFound = $"Account '{B}' not found";

    public static IEnumerable<object[]> NamingB =>
    [
        ["get_emails", $$"""{"accountId":"{{B}}"}"""],
        ["get_email_details", $$"""{"accountId":"{{B}}","emailId":"e"}"""],
        ["search_emails", $$"""{"query":"x","accountId":"{{B}}"}"""],
        ["list_calendars", $$"""{"accountId":"{{B}}"}"""],
        ["get_calendar_events", $$"""{"timeZone":"UTC","accountId":"{{B}}"}"""],
        ["get_calendar_event_details", $$"""{"timeZone":"UTC","calendarId":"primary","eventId":"{{ReferenceB}}"}"""],
        ["get_contacts", $$"""{"accountId":"{{B}}"}"""],
        ["search_contacts", $$"""{"query":"x","accountId":"{{B}}"}"""],
        ["get_contact_details", $$"""{"accountId":"{{B}}","contactId":"c"}"""],
        ["create_event", $$"""{"subject":"s","start":"2026-10-01T09:00:00Z","end":"2026-10-01T10:00:00Z","accountId":"{{B}}"}"""],
        ["update_event", $$"""{"calendarId":"primary","eventId":"{{ReferenceB}}","subject":"s"}"""],
        ["respond_to_event", $$"""{"eventId":"{{ReferenceB}}","response":"accept"}"""],
        ["send_email", $$"""{"accountId":"{{B}}","to":["x@example.org"],"subject":"s"}"""],
        ["delete_email", $$"""{"accountId":"{{B}}","emailId":"e"}"""],
        ["mark_email_read", $$"""{"accountId":"{{B}}","emailId":"e","isRead":true}"""],
        ["move_email", $$"""{"accountId":"{{B}}","emailId":"e","destination":"archive"}"""],
        ["delete_event", $$"""{"eventId":"{{ReferenceB}}"}"""],
        ["create_contact", $$"""{"displayName":"x","accountId":"{{B}}"}"""],
        ["update_contact", $$"""{"accountId":"{{B}}","contactId":"c","notes":"n"}"""],
        ["delete_contact", $$"""{"accountId":"{{B}}","contactId":"c"}"""],
        ["get_email_attachment", $$"""{"accountId":"{{B}}","emailId":"e","attachmentId":"part-0"}"""],
        ["get_unsubscribe_info", $$"""{"accountId":"{{B}}","emailId":"e"}"""],
        ["unsubscribe_from_email", $$"""{"accountId":"{{B}}","emailId":"e"}"""],
        ["bulk_delete_emails", $$"""{"items":[{"accountId":"{{B}}","emailId":"e"}]}"""],
        ["bulk_mark_emails_read", $$"""{"items":[{"accountId":"{{B}}","emailId":"e"}],"isRead":true}"""],
        ["bulk_move_emails", $$"""{"items":[{"accountId":"{{B}}","emailId":"e"}],"destination":"archive"}"""],
    ];

    /// <summary>Calls that name no account, or one only B has; the flag says whether A's provider must be reached.</summary>
    public static IEnumerable<object[]> NamingNoAccount =>
    [
        ["list_accounts", "{}", false],
        ["get_guide", "{}", false],
        ["get_contextual_email_summary", "{}", true],
        ["get_emails", "{}", true],
        ["get_calendar_events", """{"timeZone":"UTC","calendarId":"b-calendar"}""", true],
        ["create_event", """{"subject":"s","start":"2026-10-01T09:00:00Z","end":"2026-10-01T10:00:00Z"}""", true],
        ["create_contact", """{"displayName":"x"}""", true],
    ];

    [TestMethod]
    [DynamicData(nameof(NamingB))]
    public async Task ATool_RefusesAnotherTenantsAccountAsUnknown(string tool, string arguments)
    {
        var factory = new RecordingProviderFactory();

        var result = await SurfaceSession.CallAsync(factory, tool, arguments);

        // The bulk tools report each item inside their JSON result, which escapes the quotes.
        StringAssert.Contains(SurfaceSession.Text(result).Replace("\\u0027", "'"), NotFound, tool);
        Assert.IsEmpty(factory.Requested, $"{tool} reached a provider for an account it should not know.");
    }

    [TestMethod]
    [DynamicData(nameof(NamingNoAccount))]
    public async Task ATool_AnswersFromTheCallersAccountsAlone(string tool, string arguments, bool readsA)
    {
        var factory = new RecordingProviderFactory();

        var result = await SurfaceSession.CallAsync(factory, tool, arguments);

        var text = SurfaceSession.Text(result);
        Assert.DoesNotContain(B, text, tool);
        Assert.DoesNotContain(SurfaceSession.DisplayNameB, text, tool);
        CollectionAssert.DoesNotContain(factory.Requested.ToArray(), SurfaceSession.ProviderB, tool);
        if (readsA)
            CollectionAssert.Contains(factory.Requested.ToArray(), SurfaceSession.ProviderA, tool);
    }

    [TestMethod]
    public async Task ListAccounts_ListsTheCallersAccount()
    {
        var result = await SurfaceSession.CallAsync(new RecordingProviderFactory(), "list_accounts", "{}");

        StringAssert.Contains(SurfaceSession.Text(result), SurfaceSession.AccountA);
    }

    [TestMethod]
    public async Task TheTablesCoverEveryAdvertisedTool()
    {
        var covered = NamingB.Concat(NamingNoAccount).Select(row => (string)row[0]).Distinct().ToArray();

        CollectionAssert.AreEquivalent(SurfaceContractTests.ToolNames, covered);
        CollectionAssert.AreEquivalent(
            (await SurfaceSession.ListToolsAsync()).Select(t => t.Name).ToArray(), covered);
    }
}
