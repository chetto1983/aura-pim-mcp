using CalendarMcp.Core.Utilities;

namespace CalendarMcp.Tests.Utilities;

[TestClass]
public class MailBodyTests
{
    private static readonly string Newsletter =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Mail", "newsletter-large.html"));

    private static void AssertGone(string markdown, string marker) =>
        Assert.IsFalse(markdown.Contains(marker, StringComparison.Ordinal), $"'{marker}' reached the Markdown:\n{markdown}");

    [TestMethod]
    public void Newsletter_ShrinksToItsWordsAndLinks()
    {
        var markdown = MailBody.ToMarkdown(Newsletter);

        Assert.IsTrue(Newsletter.Length > 60_000, "the fixture must stay the size of the measured body");
        Assert.IsTrue(markdown.Length < 12_000, $"Markdown is {markdown.Length} characters");
        StringAssert.Contains(markdown, "Il bando regionale per le PMI riapre il 12 novembre");
        StringAssert.Contains(markdown, "| Data | Evento | Luogo |");
        StringAssert.Contains(markdown, "| 12 novembre | Apertura bando regionale | Bologna |");
        StringAssert.Contains(markdown, "](https://click.news.example.com/");
    }

    [TestMethod]
    public void Newsletter_LeavesNoMarkupBehind()
    {
        var markdown = MailBody.ToMarkdown(Newsletter);

        foreach (var marker in new[] { "<table", "<td", "<div", "<span", "<a ", "<img", "style=", "mso-", "<!--", "cdn.news.example.com" })
            AssertGone(markdown, marker);
    }

    [TestMethod]
    public void Newsletter_DropsTheTrackingAndTheHiddenPreheader()
    {
        var markdown = MailBody.ToMarkdown(Newsletter);

        foreach (var marker in new[] { "utm_", "mc_eid", "_hsenc", "open.gif", "altre sette notizie per la tua impresa" })
            AssertGone(markdown, marker);
    }

    [TestMethod]
    public void Newsletter_KeepsBlocksApart()
    {
        var markdown = MailBody.ToMarkdown(Newsletter);

        // A layout cell is a block. Unwrapping it would run "Notizie" into "Bandi".
        AssertGone(markdown, "NotizieBandi");
        StringAssert.Contains(markdown, "[Notizie](");
    }

    [TestMethod]
    public void Newsletter_ConvertsInUnderASecond()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        MailBody.ToMarkdown(Newsletter);
        MailBody.ToMarkdown(Newsletter);

        Assert.IsTrue(watch.ElapsedMilliseconds < 2_000, $"two conversions took {watch.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    [DataRow("""<p style="display:none">SECRET</p>""", "inline display")]
    [DataRow("""<p style="DISPLAY : NONE !important">SECRET</p>""", "inline display, any casing")]
    [DataRow("""<p style="visibility:hidden">SECRET</p>""", "inline visibility")]
    [DataRow("""<p hidden>SECRET</p>""", "hidden attribute")]
    [DataRow("""<p style="font-size:0">SECRET</p>""", "zero font size")]
    [DataRow("""<p style="font-size:0px">SECRET</p>""", "zero font size in px")]
    [DataRow("""<p style="font-size:0.0em">SECRET</p>""", "zero font size in em")]
    [DataRow("""<p style="font-size:1px">SECRET</p>""", "one pixel font")]
    [DataRow("""<div style="font-size:0"><span>SECRET</span></div>""", "inherited zero font size")]
    [DataRow("""<p style="opacity:0">SECRET</p>""", "zero opacity")]
    [DataRow("""<p style="color:transparent">SECRET</p>""", "transparent text")]
    [DataRow("""<p style="color:#ffffff;background-color:#ffffff">SECRET</p>""", "white on white")]
    [DataRow("""<p style="color:#ffffff">SECRET</p>""", "white on the default page")]
    [DataRow("""<table bgcolor="#123456"><tr><td style="color:#123456">SECRET</td><td>x</td></tr></table>""", "text the colour of its table")]
    [DataRow("""<p style="max-height:0;overflow:hidden">SECRET</p>""", "collapsed box")]
    [DataRow("""<p style="height:0;width:0;overflow:hidden">SECRET</p>""", "zero box")]
    [DataRow("""<p style="position:absolute;left:-9999px">SECRET</p>""", "off screen")]
    [DataRow("""<p style="text-indent:-9999px">SECRET</p>""", "indented off screen")]
    [DataRow("""<p style="clip:rect(0,0,0,0);position:absolute">SECRET</p>""", "clipped to nothing")]
    [DataRow("""<p style="mso-hide:all">SECRET</p>""", "hidden from Outlook")]
    [DataRow("""<style>.x{display:none}</style><p class="x">SECRET</p>""", "stylesheet display")]
    [DataRow("""<style>.x{visibility:hidden}</style><p class="x">SECRET</p>""", "stylesheet visibility")]
    [DataRow("""<style>.x{font-size:0}</style><p class="x">SECRET</p>""", "stylesheet font size")]
    [DataRow("""<style>.x{opacity:0}</style><p class="x">SECRET</p>""", "stylesheet opacity")]
    [DataRow("""<style>div p{color:#fff}</style><div><p>SECRET</p></div>""", "stylesheet colour")]
    [DataRow("""<template><p>SECRET</p></template>""", "template")]
    [DataRow("""<noscript><p>SECRET</p></noscript>""", "noscript")]
    [DataRow("""<!-- SECRET -->""", "comment")]
    [DataRow("""<script>var a = "SECRET";</script>""", "script")]
    [DataRow("""<title>SECRET</title>""", "title")]
    [DataRow("""<img src="https://x.example.com/a.png" alt="SECRET" style="display:none">""", "alt of a hidden image")]
    public void HiddenText_NeverReachesTheModel(string hidden, string method)
    {
        var markdown = MailBody.ToMarkdown($"<html><body><p>Visible words</p>{hidden}</body></html>");

        StringAssert.Contains(markdown, "Visible words", method);
        AssertGone(markdown, "SECRET");
        AssertGone(MailBody.ToPlainText($"<html><body><p>Visible words</p>{hidden}</body></html>"), "SECRET");
    }

    [TestMethod]
    [DataRow("""<table role="presentation"><tr><td style="font-size:0;line-height:0"><div style="display:inline-block"><table role="presentation"><tr><td style="font-size:16px">SHOWN</td></tr></table></div></td></tr></table>""", "ghost column")]
    [DataRow("""<div style="font-size:0"><span style="font-size:14px">SHOWN</span></div>""", "font size reset by a child")]
    [DataRow("""<div style="visibility:hidden"><span style="visibility:visible">SHOWN</span></div>""", "visibility reset by a child")]
    [DataRow("""<td bgcolor="#0b5cad" style="color:#ffffff">SHOWN</td>""", "white on a coloured cell")]
    [DataRow("""<div style="background-image:url(https://x.example.com/hero.jpg);color:#ffffff">SHOWN</div>""", "white on an image")]
    [DataRow("""<div style="background-color:#222222"><p style="color:#ffffff">SHOWN</p></div>""", "white on a dark block")]
    [DataRow("""<p style="max-height:0">SHOWN</p>""", "a max-height with nothing clipping it")]
    public void RenderedText_IsNotMistakenForHiddenText(string shown, string layout)
    {
        var markdown = MailBody.ToMarkdown($"<html><body>{shown}</body></html>");

        StringAssert.Contains(markdown, "SHOWN", layout);
    }

    [TestMethod]
    [DataRow("""<td bgcolor="rgb(9999999999,0,0)">Words</td>""")]
    [DataRow("""<td bgcolor="rgb(0,0,0,1.2.3)">Words</td>""")]
    [DataRow("""<td bgcolor="rgb(99999999999999999999,99999999999999999999,99999999999999999999,99999999999999999999)">Words</td>""")]
    [DataRow("""<p style="font-size:99999999999999999999999999999999999999999999px">Words</p>""")]
    [DataRow("""<p style="height:99999999999999999999999999999999999999999999px;overflow:hidden">Words</p>""")]
    [DataRow("""<p style="text-indent:-99999999999999999999999999999999999999999999">Words</p>""")]
    public void AHostileNumber_IsNoReasonToFail(string html)
    {
        StringAssert.Contains(MailBody.ToPlainText($"<html><body><table><tr>{html}</tr></table></body></html>"), "Words");
    }

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("JaVaScRiPt:alert(1)")]
    [DataRow("data:text/html;base64,AAAA")]
    [DataRow("vbscript:msgbox(1)")]
    [DataRow("file:///etc/passwd")]
    public void LinkWithAForbiddenScheme_KeepsItsWordsAndLosesItsTarget(string href)
    {
        var markdown = MailBody.ToMarkdown($"""<p><a href="{href}">Press here</a></p>""");

        StringAssert.Contains(markdown, "Press here");
        AssertGone(markdown, "](");
        AssertGone(markdown, href);
    }

    [TestMethod]
    public void HttpHttpsAndMailtoLinksSurvive()
    {
        var markdown = MailBody.ToMarkdown(
            """<p><a href="http://a.example.com/">one</a> <a href="https://b.example.com/">two</a> <a href="mailto:c@example.com">three</a></p>""");

        StringAssert.Contains(markdown, "[one](http://a.example.com/)");
        StringAssert.Contains(markdown, "[two](https://b.example.com/)");
        StringAssert.Contains(markdown, "[three](mailto:c@example.com)");
    }

    [TestMethod]
    [DataRow("https://a.example.com/x?utm_source=n&id=7&fbclid=abc", "https://a.example.com/x?id=7")]
    [DataRow("https://a.example.com/x?UTM_Campaign=n", "https://a.example.com/x")]
    [DataRow("https://a.example.com/x?gclid=1&msclkid=2&mc_eid=3&_hsenc=4#part", "https://a.example.com/x#part")]
    [DataRow("https://a.example.com/reset?token=abc123", "https://a.example.com/reset?token=abc123")]
    [DataRow("https://a.example.com/x?utmost=1", "https://a.example.com/x?utmost=1")]
    public void TrackingParameters_AreCutAndWorkingOnesKept(string href, string expected)
    {
        Assert.AreEqual(expected, MailBody.WithoutTrackingParameters(href));
    }

    [TestMethod]
    public void DataTable_StaysATable_AndLayoutTableDoesNot()
    {
        var markdown = MailBody.ToMarkdown(
            """
            <table><tr><td><table><tr><th>Item</th><th>Price</th></tr><tr><td>Bolt</td><td>2</td></tr></table></td></tr></table>
            <table><tr><td>Just one column</td></tr><tr><td>Still layout</td></tr></table>
            """);

        StringAssert.Contains(markdown, "| Item | Price |");
        StringAssert.Contains(markdown, "| Bolt | 2 |");
        AssertGone(markdown, "| Just one column");
    }

    [TestMethod]
    public void Images_BecomeTheirAltTextOrNothing()
    {
        var markdown = MailBody.ToMarkdown(
            """<p><img src="https://cdn.example.com/x.png" alt="Quarterly chart"><img src="data:image/png;base64,AAAA" alt=""><img src="https://t.example.com/p.gif" width="1" height="1" alt=""></p>""");

        StringAssert.Contains(markdown, "Quarterly chart");
        AssertGone(markdown, "cdn.example.com");
        AssertGone(markdown, "data:");
        AssertGone(markdown, "t.example.com");
    }

    [TestMethod]
    public void UnknownTags_DoNotLeakIntoTheMarkdown()
    {
        var markdown = MailBody.ToMarkdown("""<center><font color="red">Words</font></center>""");

        Assert.AreEqual("Words", markdown);
    }

    [TestMethod]
    public void InvisibleCharacters_AreRemoved()
    {
        // Zero-width marks and Unicode tag characters render as nothing and still reach a model.
        var tagged = "Hello" + char.ConvertFromUtf32(0xE0041) + char.ConvertFromUtf32(0xE0042) + "​‮ world";

        Assert.AreEqual("Hello world", MailBody.ToMarkdown($"<p>{tagged}</p>"));
        Assert.AreEqual("Hello world", MailBody.ToPlainText($"<p>{tagged}</p>"));
    }

    [TestMethod]
    public void ForModel_ConvertsHtmlAndNamesTheFormat()
    {
        var (body, format) = MailBody.ForModel("<p><b>Hi</b></p>", "html");

        Assert.AreEqual("**Hi**", body);
        Assert.AreEqual("markdown", format);
    }

    [TestMethod]
    public void ForModel_LeavesPlainTextExactlyAlone()
    {
        const string plain = "Line one\n\n<b>not markup</b>\nhttps://a.example.com/?utm_source=keep";

        var (body, format) = MailBody.ForModel(plain, "text");

        Assert.AreEqual(plain, body);
        Assert.AreEqual("text", format);
    }

    [TestMethod]
    public void ForModel_EmptyHtml_IsAnEmptyBody()
    {
        Assert.AreEqual(string.Empty, MailBody.ForModel("", "html").Body);
    }

    [TestMethod]
    public void PlainText_HasNoMarkdownAndKeepsWordsApart()
    {
        var text = MailBody.ToPlainText(
            """<h1>Title</h1><p>First <b>bold</b> <a href="https://a.example.com/?id=1">link</a></p><ul><li>One</li><li>Two</li></ul><table><tr><th>A</th><th>B</th></tr></table>""");

        Assert.AreEqual("Title First bold link One Two A B", text);
    }
}
