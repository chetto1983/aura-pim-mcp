using System.Buffers.Text;
using System.Text;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Google;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Utilities;
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

    private ChangesPipeline Pipeline(TimeProvider? clock = null) =>
        new([TestData.CreateAccount(id: Mine, provider: "google")], [new GoogleMailChanges(Provider(_http), clock)]);

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

    private async Task<Dictionary<string, string>> SnippetsAsync(TimeProvider? clock = null)
    {
        await using var pim = Pipeline(clock);
        var (status, body) = await pim.PostAsync("/admin/changes", new { accountId = Mine, resource = "mail", cursor = Cursor });

        Assert.AreEqual(200, status, "a mail must never fail its page");
        Assert.AreNotEqual(Cursor, body.GetProperty("nextCursor").GetString(), "the cursor has to move past it");

        return body.GetProperty("changes").EnumerateArray().ToDictionary(
            change => change.GetProperty("id").GetString()!,
            change => change.GetProperty("snippet").GetString()!);
    }

    private const int JustUnderTheCap = MailBody.MaxBodyLength - 16;

    private static string Flood(string unit, int length = JustUnderTheCap) =>
        string.Concat(Enumerable.Repeat(unit, length / unit.Length));

    private static string Chain(string call) =>
        "<style>:root{" + string.Concat(Enumerable.Range(0, 8000).Select(i => $"--v{i}:calc({call}(--v{i + 1}) + 1px);"))
        + "--v8000:1px}p{width:" + call + "(--v0)}</style><p>Parole</p>";

    public static IEnumerable<object[]> MailsTooDeepToRead =>
    [
        ["unclosed inline tags, no text", Flood("<i>")],
        ["unclosed blocks, no text", Flood("<section>")],
        ["text at every level", Flood("<div>x")],
        ["text at the bottom alone", Flood("<div>", 200_000) + "Parole"],
        ["tables in tables", Flood("<table><tr><td>")],
        ["svg", Flood("<svg><g>")],
        ["a few thousand levels", Flood("<b>", 12_000) + "Parole"],
        ["tags the parser recurses on", Flood("<pre>")],
    ];

    /// <summary>The first thing a hostile mail tries: a stack overflow cannot be caught, and it takes every tenant with it.</summary>
    [TestMethod]
    [DynamicData(nameof(MailsTooDeepToRead))]
    public async Task AMailTooDeepToRead_ArrivesWithNoSnippet(string shape, string html)
    {
        Delivers(("deep", html), ("next", "<p>La mail dopo</p>"));

        var snippets = await SnippetsAsync();

        Assert.AreEqual(string.Empty, snippets["deep"], shape);
        Assert.AreEqual("La mail dopo", snippets["next"]);
    }

    public static IEnumerable<object[]> StylesheetsThatKillTheEngine =>
    [
        ["nested @media", $"<style>{Flood("@media screen{", 400_000)}</style>"],
        ["nested @supports", $"<style>{Flood("@supports (color:red){", 400_000)}</style>"],
        ["nested @layer", $"<style>{Flood("@layer a{", 400_000)}</style>"],
        ["nested rules", $"<style>{Flood("p{&", 400_000)}</style>"],
        ["nested :has()", $"<style>{Flood(":has(", 400_000)}a{{color:red}}</style>"],
        ["nested calc()", $"<style>p{{width:{Flood("calc(", 400_000)}1px}}</style>"],
        ["brackets in calc()", $"<style>p{{width:calc({Flood("(", 400_000)}1px}}</style>"],
        ["calc() with no nesting at all", $"<style>p{{width:calc({Flood("1px+", 400_000)}1px)}}</style>"],
        ["a comment closing nothing", $"<style>p{{width:{Flood("calc(/*)*/", 400_000)}1px}}</style>"],
        ["a string closing nothing", $"<style>p{{width:{Flood("calc(\")\"", 400_000)}1px}}</style>"],
        ["an inline style", $"""<p style="width:{Flood("calc(", 400_000)}1px">Parole</p>"""],
        ["an inline style in entities", $"""<p style="width:{Flood("calc&#40;", 400_000)}1px">Parole</p>"""],
        ["a media attribute", $$"""<style media="{{Flood("(", 400_000)}}">p{color:red}</style>"""],
        ["a chain of variables", Chain("var")],
        ["a chain of escaped variables", Chain("v\\61r")],
    ];

    /// <summary>The HTML parser's depth limit does not reach CSS. Each of these aborts the process at this size unguarded.</summary>
    [TestMethod]
    [DynamicData(nameof(StylesheetsThatKillTheEngine))]
    public async Task AStylesheetThatWouldKillTheEngine_ArrivesWithNoSnippet(string shape, string html)
    {
        Delivers(("hostile", $"{html}<p>Parole</p>"), ("next", "<p>La mail dopo</p>"));

        var snippets = await SnippetsAsync();

        Assert.AreEqual(string.Empty, snippets["hostile"], shape);
        Assert.AreEqual("La mail dopo", snippets["next"]);
    }

    [TestMethod]
    public async Task ABodyOverTheCap_ArrivesWithNoSnippet_OnAProviderWithNoCapOfItsOwn()
    {
        Delivers(("huge", $"<p>Parole</p>{Flood("x", MailBody.MaxBodyLength + 1)}"), ("next", "<p>La mail dopo</p>"));

        var snippets = await SnippetsAsync();

        Assert.AreEqual(string.Empty, snippets["huge"]);
        Assert.AreEqual("La mail dopo", snippets["next"]);
    }

    [TestMethod]
    public async Task ManyCssRules_StayInsideThePage()
    {
        var rules = string.Concat(Enumerable.Range(0, 40_000).Select(i => $".c{i}{{color:red}}\n"));
        var paragraphs = Flood("<p>x</p>", 100_000);
        Delivers(("rules", $"<style>{rules}</style>{paragraphs}<p>Parole</p>"), ("next", "<p>La mail dopo</p>"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var snippets = await SnippetsAsync();

        Assert.AreEqual("La mail dopo", snippets["next"]);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");
    }

    [TestMethod]
    public async Task AFloodOfBrackets_StaysInsideThePage()
    {
        Delivers(("brackets", $"<p>{Flood("[")}</p>"), ("next", "<p>La mail dopo</p>"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var snippets = await SnippetsAsync();

        Assert.AreEqual("La mail dopo", snippets["next"]);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");
    }

    [TestMethod]
    public async Task APageOutOfTime_ReturnsEveryMailAndMovesItsCursor()
    {
        var mails = Enumerable.Range(0, 5).Select(i => ($"m{i}", "<p>Parole</p>")).ToArray();
        Delivers(mails);

        var snippets = await SnippetsAsync(new SteppingClock(SnippetPage.Allowed / 3));

        CollectionAssert.AreEqual(
            new[] { "Parole", "Parole", "Parole", string.Empty, string.Empty },
            mails.Select(mail => snippets[mail.Item1]).ToArray());
    }

    [TestMethod]
    public async Task ABodyGmailCannotDecode_LosesOnlyItsSnippet()
    {
        Delivers(("next", "<p>La mail dopo</p>"), ("broken", "<p>Parole</p>"));
        _http.On("/users/me/messages/broken", Json(new
        {
            id = "broken",
            labelIds = new[] { Inbox },
            payload = new
            {
                mimeType = "text/html",
                headers = new[] { new { name = "From", value = "Sconosciuto <x@example.com>" } },
                body = new { data = "not base64 at all ===" }
            }
        }));

        var snippets = await SnippetsAsync();

        Assert.AreEqual(string.Empty, snippets["broken"]);
        Assert.AreEqual("La mail dopo", snippets["next"]);
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
