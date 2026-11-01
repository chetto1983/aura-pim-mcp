using System.Buffers.Text;
using System.Text;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Google;
using CalendarMcp.Core.Models;
using CalendarMcp.Tests.Helpers;
using static CalendarMcp.Tests.Changes.GmailFixtures;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// Mail written to hurt, through the whole pipeline. The rule these pin is the contract's: one mail
/// never blocks a page, because a page that fails is replayed forever and every later mail waits.
/// </summary>
[TestClass]
public class HostileMailTests
{
    private const string Inbox = "INBOX";
    private const string Mine = "gmail";

    private readonly FakeHttp _http = new();

    private ChangesPipeline Pipeline() =>
        new([TestData.CreateAccount(id: Mine, provider: "google")], [new GoogleMailChanges(Provider(_http))]);

    private static string Cursor => ChangesCursor.Of("google.mail", Inbox, new { history = 100UL }).Encode();

    private void Delivers(params (string Id, string Html)[] mails)
    {
        _http.NoSentMail().OnHistory("startHistoryId=100", new
        {
            historyId = "200",
            history = new[] { new { id = "150", messagesAdded = mails.Select(mail => Added(mail.Id, Inbox)).ToArray() } }
        });

        foreach (var (id, html) in mails)
        {
            _http.On($"/users/me/messages/{id}", Json(new
            {
                id,
                labelIds = new[] { Inbox },
                payload = new
                {
                    mimeType = "text/html",
                    headers = new[] { new { name = "From", value = "Sconosciuto <x@example.com>" }, new { name = "Subject", value = id } },
                    body = new { data = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(html)) }
                }
            }));
        }
    }

    private async Task<Dictionary<string, string>> SnippetsAsync()
    {
        await using var pim = Pipeline();
        var (status, body) = await pim.PostAsync("/admin/changes", new { accountId = Mine, resource = "mail", cursor = Cursor });

        Assert.AreEqual(200, status, "a mail must never fail its page");
        Assert.AreNotEqual(Cursor, body.GetProperty("nextCursor").GetString(), "the cursor has to move past it");

        return body.GetProperty("changes").EnumerateArray().ToDictionary(
            change => change.GetProperty("id").GetString()!,
            change => change.GetProperty("snippet").GetString()!);
    }

    [TestMethod]
    [DataRow("""<table bgcolor="rgb(9999999999,0,0)"><tr><td>Parole</td></tr></table>""", "a colour too long for an int")]
    [DataRow("""<table bgcolor="rgb(0,0,0,1.2.3)"><tr><td>Parole</td></tr></table>""", "an alpha that is no number")]
    [DataRow("""<table bgcolor="rgba(1,2,3,.)"><tr><td>Parole</td></tr></table>""", "an alpha of a lone dot")]
    public async Task AHostileAttribute_NeitherStopsThePageNorLosesTheMail(string html, string why)
    {
        Delivers(("hostile", html), ("next", "<p>La mail dopo</p>"));

        var snippets = await SnippetsAsync();

        Assert.AreEqual("Parole", snippets["hostile"], why);
        Assert.AreEqual("La mail dopo", snippets["next"]);
    }
}
