using CalendarMcp.Tests.Helpers;

namespace CalendarMcp.Tests.Tools;

/// <summary>
/// A schema's <c>required</c> stops a missing argument, not an empty one. Each tool refuses an
/// empty target itself, before any account or provider is reached.
/// </summary>
[TestClass]
public sealed class EmptyRequiredArgumentTests
{
    private const string AskWhich =
        ": call list_accounts and pass its accountId. " +
        "When the user has more than one account and has not named one, ask which.";

    [TestMethod]
    [DataRow("send_email", """{"accountId":"a-account","to":["x@example.org"],"subject":""}""", "subject is required")]
    [DataRow("create_event", """{"accountId":"a-account","subject":"","start":"2026-10-01T09:00:00Z","end":"2026-10-01T10:00:00Z"}""", "subject is required")]
    [DataRow("create_contact", """{"accountId":"a-account","displayName":""}""", "displayName is required")]
    [DataRow("create_event", """{"accountId":"","subject":"s","start":"2026-10-01T09:00:00Z","end":"2026-10-01T10:00:00Z"}""", "create_event requires accountId, the account to create the event in" + AskWhich)]
    [DataRow("create_event", """{"accountId":"   ","subject":"s","start":"2026-10-01T09:00:00Z","end":"2026-10-01T10:00:00Z"}""", "create_event requires accountId, the account to create the event in" + AskWhich)]
    [DataRow("create_contact", """{"accountId":"","displayName":"x"}""", "create_contact requires accountId, the account to create the contact in" + AskWhich)]
    [DataRow("create_contact", """{"accountId":"   ","displayName":"x"}""", "create_contact requires accountId, the account to create the contact in" + AskWhich)]
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

    [TestMethod]
    [DataRow("send_email", """{"to":["x@example.org"],"subject":"s"}""")]
    [DataRow("create_event", """{"subject":"s","start":"2026-10-01T09:00:00Z","end":"2026-10-01T10:00:00Z"}""")]
    [DataRow("create_contact", """{"displayName":"x"}""")]
    public async Task AMissingAccountId_NamesItAndSendsTheModelToListAccounts(string tool, string arguments)
    {
        var factory = new RecordingProviderFactory();

        var result = await SurfaceSession.CallAsync(factory, tool, arguments);

        Assert.IsTrue(result.IsError, SurfaceSession.Text(result));
        StringAssert.Contains(SurfaceSession.Text(result), $"Required parameter 'accountId' was not provided to '{tool}'.");
        StringAssert.Contains(SurfaceSession.Text(result), "list_accounts");
        Assert.IsEmpty(factory.Requested);
    }

    [TestMethod]
    public async Task AnEmailToolMissingAccountId_PointsAtTheEmailListingAsWellAsListAccounts()
    {
        var result = await SurfaceSession.CallAsync(new RecordingProviderFactory(), "get_email_details", """{"emailId":"e-1"}""");

        Assert.IsTrue(result.IsError, SurfaceSession.Text(result));
        StringAssert.Contains(SurfaceSession.Text(result), "Required parameter 'accountId' was not provided to 'get_email_details'.");
        StringAssert.Contains(SurfaceSession.Text(result), "list_accounts");
        StringAssert.Contains(SurfaceSession.Text(result), "from the email's listing");
    }
}
