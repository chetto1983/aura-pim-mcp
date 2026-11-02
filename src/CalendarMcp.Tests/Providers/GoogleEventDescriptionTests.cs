using System.Text.Json;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Rocks;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// Google's <c>events</c> reference says a description "can contain HTML" and gives no type, so an
/// invitation written as HTML must not be labelled text and walk past the hidden-text filter.
/// </summary>
[TestClass]
public class GoogleEventDescriptionTests
{
    private static GoogleProviderService Google(FakeHttp http) =>
        new(NullLogger<GoogleProviderService>.Instance, new IAccountRegistryCreateExpectations().Instance())
        {
            Transport = new FakeGoogleTransport(http)
        };

    [TestMethod]
    [DataRow("""<p>Ci vediamo</p><div style="display:none">SECRET</div>""", "html")]
    [DataRow("Ci vediamo<br>domani", "html")]
    [DataRow("""<a href="https://a.example.com/">qui</a>""", "html")]
    [DataRow("<!-- nascosto -->Ci vediamo", "html")]
    [DataRow("Ci vediamo alle 3, se a < b e 5 > 4", "text")]
    [DataRow("Ci vediamo\nalle 3", "text")]
    [DataRow("", "text")]
    public async Task ADescription_IsReadAsWhatItHolds(string description, string format)
    {
        var body = JsonSerializer.Serialize(new
        {
            id = "ev",
            summary = "Invito",
            description,
            start = new { dateTime = "2026-11-02T15:00:00Z" },
            end = new { dateTime = "2026-11-02T16:00:00Z" }
        });
        var http = new FakeHttp().On("/calendars/primary/events/ev", body);

        var details = await Google(http).GetCalendarEventDetailsAsync("a", "primary", "ev");

        Assert.AreEqual(format, details!.BodyFormat);
        Assert.AreEqual(description, details.Body);
    }
}
