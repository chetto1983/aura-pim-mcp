using CalendarMcp.Tests.Helpers;

namespace CalendarMcp.Tests.Tools;

/// <summary>
/// A schema's <c>required</c> stops a missing argument, not an empty one. Each tool refuses an
/// empty target itself, before any account or provider is reached.
/// </summary>
[TestClass]
public sealed class EmptyRequiredArgumentTests
{
    [TestMethod]
    [DataRow("send_email", """{"accountId":"a-account","to":["x@example.org"],"subject":""}""", "subject is required")]
    [DataRow("create_event", """{"subject":"","start":"2026-10-01T09:00:00Z","end":"2026-10-01T10:00:00Z"}""", "subject is required")]
    [DataRow("create_contact", """{"displayName":""}""", "displayName is required")]
    [DataRow("get_calendar_event_details", """{"timeZone":"UTC","calendarId":"","eventId":"ref"}""", "calendarId is required")]
    [DataRow("delete_event", """{"eventId":""}""", "eventId is required")]
    [DataRow("delete_email", """{"accountId":"","emailId":"e"}""", "accountId is required")]
    [DataRow("delete_contact", """{"accountId":"","contactId":"c"}""", "accountId is required")]
    [DataRow("unsubscribe_from_email", """{"accountId":"","emailId":"e"}""", "accountId is required")]
    [DataRow("bulk_delete_emails", """{"items":[]}""", "items array must not be empty")]
    [DataRow("bulk_move_emails", """{"items":[{"accountId":"a-account","emailId":"e"}],"destination":""}""", "destination is required")]
    public async Task AnEmptyTarget_IsRefusedBeforeAnyProvider(string tool, string arguments, string expected)
    {
        var factory = new RecordingProviderFactory();

        var result = await SurfaceSession.CallAsync(factory, tool, arguments);

        Assert.IsTrue(result.IsError, SurfaceSession.Text(result));
        Assert.AreEqual($"An error occurred invoking '{tool}': {expected}", SurfaceSession.Text(result));
        Assert.IsEmpty(factory.Requested);
    }
}
