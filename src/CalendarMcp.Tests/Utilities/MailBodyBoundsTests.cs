using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Utilities;

namespace CalendarMcp.Tests.Utilities;

/// <summary>
/// What a conversion may cost. The parser's depth limit moves the children of an element at the limit
/// out of it, so a limit alone would lift text out of a hidden ancestor: a body that reaches it is
/// refused whole. The stack tests run small on purpose, and an overflow takes the test host down.
/// </summary>
[TestClass]
public class MailBodyBoundsTests
{
    /// <summary>
    /// An eighth of the 8 MiB a pool thread has in the image, and 1.7 times the 591 KB the worst mail needs
    /// in a fresh process. The need moves with the tier the code has reached, and the JIT runs on this
    /// stack too, so anything under that worst case passes only once the code has warmed up: 256 KB did,
    /// until the one run that reached a deep mail first and overflowed.
    /// </summary>
    internal const int SmallStack = 1024 * 1024;

    /// <summary>html is 1 and body is 2, so this many wrappers put what follows at this depth plus 3.</summary>
    private static string Nested(int wrappers, string inner) =>
        string.Concat(Enumerable.Repeat("<div>", wrappers)) + inner;

    private static void OnASmallStack(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = e;
            }
        }, SmallStack);

        thread.Start();
        thread.Join();
        Assert.IsNull(failure, failure?.ToString());
    }

    public static IEnumerable<object[]> DepthsAroundTheLimit =>
        Enumerable.Range(MailBody.MaxDepth - 8, 16).Select(depth => new object[] { depth });

    [TestMethod]
    [DynamicData(nameof(DepthsAroundTheLimit))]
    public void HiddenText_IsNeverLiftedOutOfItsAncestor(int depth)
    {
        foreach (var hidden in new[]
        {
            """<div style="display:none"><p>SECRET</p></div>""",
            """<div hidden><p>SECRET</p><p>SECRET</p></div>""",
            """<style>.x{display:none}</style><div class="x"><span>SECRET</span></div>""",
            """<div style="font-size:0"><b><i>SECRET</i></b></div>""",
            """<div style="visibility:hidden"><p>SECRET</p></div>""",
            """<div style="opacity:0"><div><div><p>SECRET</p></div></div></div>""",
            """<template><p>SECRET</p></template>""",
            """<table style="display:none"><tr><td><p>SECRET</p></td></tr></table>""",
        })
        {
            var html = Nested(depth - 3, hidden);

            Assert.IsFalse(MailSnippet.Of(html, MailBody.HtmlFormat).Contains("SECRET", StringComparison.Ordinal), hidden);
            Assert.IsFalse(MailBody.ForModel(html, MailBody.HtmlFormat).Body.Contains("SECRET", StringComparison.Ordinal), hidden);
        }
    }

    [TestMethod]
    public void TheLimit_IsRefusedAndNotConverted()
    {
        var html = Nested(MailBody.MaxDepth - 2, "Parole");

        var (body, format) = MailBody.ForModel(html, MailBody.HtmlFormat);

        Assert.AreEqual(MailBody.OmittedFormat, format);
        StringAssert.Contains(body, "nested too deeply");
        Assert.IsFalse(body.Contains("Parole", StringComparison.Ordinal));
    }

    [TestMethod]
    public void JustInsideTheLimit_ConvertsOnASmallStack()
    {
        var html = Nested(MailBody.MaxDepth - 3, "Parole");

        OnASmallStack(() =>
        {
            Assert.AreEqual("Parole", MailBody.ToMarkdown(html));
            Assert.AreEqual("Parole", MailBody.ToPlainText(html));
        });
    }

    public static IEnumerable<object[]> NestingThatReadersRecurseOn =>
    [
        ["<blockquote>"],
        ["<ul><li>"],
        ["<table><tbody><tr><td>"],
        ["<b>"],
        // A link closes the link before it, unless a scope marker stands between them.
        ["<a href=\"https://a.example.com/\"><object>"],
        ["<dl><dd>"],
        ["<div>"],
        ["<section id=\"c\">"],
        ["<blockquote><ul><li>"],
    ];

    /// <summary>ReverseMarkdown's <c>ReadChildren</c> and its writers recurse, each level with its own reader.</summary>
    [TestMethod]
    [DynamicData(nameof(NestingThatReadersRecurseOn))]
    public void WhateverFitsTheLimit_ReadsOnASmallStack(string level)
    {
        var levels = (MailBody.MaxDepth - 3) / (level.Split('<').Length - 1);
        var html = string.Concat(Enumerable.Repeat(level, levels)) + "Parole";

        OnASmallStack(() =>
        {
            Assert.AreEqual(MailBody.MarkdownFormat, MailBody.ForModel(html, MailBody.HtmlFormat).Format, level);
            StringAssert.Contains(MailSnippet.Of(html, MailBody.HtmlFormat), "Parole", level);
        });
    }

    /// <summary>The parser itself recurses on these, one level a tag, before there is a tree to limit.</summary>
    [TestMethod]
    [DataRow("<pre>")]
    [DataRow("<listing>")]
    [DataRow("<pre><listing>")]
    public void TagsTheParserRecursesOn_AreRefusedOnASmallStack(string level)
    {
        var html = string.Concat(Enumerable.Repeat(level, (MailBody.MaxBodyLength - 8) / level.Length)) + "Parole";

        OnASmallStack(() =>
        {
            var (body, format) = MailBody.ForModel(html, MailBody.HtmlFormat);

            Assert.AreEqual(MailBody.OmittedFormat, format, level);
            StringAssert.Contains(body, "nested too deeply", level);
            Assert.AreEqual(string.Empty, MailSnippet.Of(html, MailBody.HtmlFormat), level);
        });
    }

    [TestMethod]
    public void APreInsideTheLimit_IsAnOrdinaryPre()
    {
        var html = string.Concat(Enumerable.Repeat("<pre>", 8)) + "Parole";

        Assert.AreEqual(MailBody.MarkdownFormat, MailBody.ForModel(html, MailBody.HtmlFormat).Format);
        StringAssert.Contains(MailSnippet.Of(html, MailBody.HtmlFormat), "Parole");
    }

    [TestMethod]
    public void AToolReadingPastTheCap_SaysTheBodyIsTooLarge()
    {
        var (body, format) = MailBody.ForModel(new string('x', MailBody.MaxBodyLength + 1), MailBody.HtmlFormat);

        Assert.AreEqual(MailBody.OmittedFormat, format);
        StringAssert.Contains(body, "too large");
        Assert.IsFalse(body.Contains("xxx", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TheCap_IsExact()
    {
        const string html = "<p>Parole</p>";

        StringAssert.Contains(MailBody.ToMarkdown(html + new string(' ', MailBody.MaxBodyLength - html.Length)), "Parole");
        Assert.AreEqual(
            MailBody.OmittedFormat,
            MailBody.ForModel(html + new string(' ', MailBody.MaxBodyLength - html.Length + 1), MailBody.HtmlFormat).Format);
    }

    [TestMethod]
    public void ASnippetOverTheCap_IsEmptyForPlainTextToo()
    {
        Assert.AreEqual(string.Empty, MailSnippet.Of(new string('x', MailBody.MaxBodyLength + 1), "text"));
    }

    [TestMethod]
    public void ABudgetAlreadySpent_StopsTheConversion()
    {
        var spent = new ConversionBudget(TimeSpan.Zero);

        Assert.ThrowsExactly<UnconvertibleMailException>(() => MailBody.ToPlainText("<p>x</p>", spent));
        Assert.ThrowsExactly<UnconvertibleMailException>(() => MailBody.ToMarkdown("<p>x</p>", spent));
    }

    [TestMethod]
    public void TheCallerGoingAway_IsNotAConversionFailure()
    {
        using var gone = new CancellationTokenSource();
        gone.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => MailBody.ToPlainText("<p>x</p>", new ConversionBudget(TimeSpan.FromMinutes(1), gone.Token)));
    }

    private static string Repeated(string unit, int times) => string.Concat(Enumerable.Repeat(unit, times));

    public static IEnumerable<object[]> SlowMails =>
    [
        ["open elements the parser holds on to", Repeated("<i>", 340_000)],
        ["text and tags set aside from a table", "<table>" + Repeated("x<b>", 250_000)],
        ["cells to move out of a table", "<table>" + Repeated("<tr><td>x", 110_000)],
        ["hidden text to remove", "<div hidden>" + Repeated("x<br>", 200_000)],
        ["a selector for everything", "<style>*{color:red}</style>" + Repeated("<p>x", 240_000)],
    ];

    [TestMethod]
    [DynamicData(nameof(SlowMails))]
    public void ASlowMail_IsCutOffCloseToItsBudget(string shape, string html)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        MailSnippet.Of(html, MailBody.HtmlFormat);

        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(3), $"{shape}: {watch.Elapsed}");
    }

    [TestMethod]
    public void UnclosedScripts_DoNotReachAnUnboundedRegex()
    {
        var html = Repeated("<script>", 130_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        MailBody.ForModel(html, MailBody.HtmlFormat);

        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");
    }
}
