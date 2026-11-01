using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Html.Parser;
using CalendarMcp.Core.Utilities;

namespace CalendarMcp.Tests.Utilities;

/// <summary>
/// AngleSharp.Css recurses once per level with no limit, so anything refused here would abort the
/// process if it reached the engine. A bracket in a comment or a string closes nothing, and the
/// check has to hold wherever the tokenizer could be, including where that is not obvious.
/// </summary>
[TestClass]
public class CssShapeTests
{
    /// <summary>Over the nesting limit, and short enough that the run limit does not do its work for it.</summary>
    private const int Levels = CssShape.MaxNesting + 8;

    private static string Times(string unit, int count = Levels) => string.Concat(Enumerable.Repeat(unit, count));

    [TestMethod]
    [DataRow("@media screen{", "at-rules")]
    [DataRow("@supports (color:red){", "at-rules with brackets")]
    [DataRow("p{&", "nested rules")]
    [DataRow(":has(", "selectors")]
    [DataRow("calc(", "functions")]
    [DataRow("(", "brackets")]
    [DataRow("[", "square brackets")]
    [DataRow("calc(/*)*/", "a comment hiding the closer")]
    [DataRow("calc(\")\"", "a string hiding the closer")]
    [DataRow("calc(')'", "the other string hiding the closer")]
    [DataRow("calc(\"\\\")\"", "an escaped quote keeping the string open")]
    [DataRow("calc(\\)", "an escaped closer")]
    [DataRow("calc(a\\)b", "an escaped closer in a word")]
    [DataRow("url(a\"b)calc(", "a quote in a url, which starts no string there")]
    [DataRow("url(a'b)calc(", "the other quote in a url")]
    [DataRow("url(\"a\" \")calc(", "a quoted url gone bad, which reads through the next quote")]
    [DataRow("url(/*)calc(*/", "a comment in a url, which is no comment there")]
    [DataRow("calc(u\\72l(\")\")", "an escaped url")]
    [DataRow("calc(u\\72 l(\")\")", "an escaped url with the space that ends the escape")]
    [DataRow("\"\ncalc(", "a line break ending a string")]
    [DataRow("\"\rcalc(", "a carriage return ending a string")]
    [DataRow("\"\fcalc(", "a form feed ending a string")]
    [DataRow("calc(\"\\\n)\"", "an escaped line break keeping a string open")]
    [DataRow("calc(\"\n)\"", "a line break a value's text reads through")]
    [DataRow("#url(", "a hash that only looks like a url")]
    [DataRow("@url(", "an at-word that only looks like a url")]
    [DataRow("1url(", "a dimension that only looks like a url")]
    [DataRow("-url(", "an identifier that only looks like a url")]
    [DataRow("\\url(", "an escape in front of a url")]
    public void DeepNesting_IsRefusedHoweverItIsDressed(string level, string why)
    {
        Assert.IsFalse(CssShape.IsSafe(Times(level)), why);
    }

    [TestMethod]
    [DataRow("1px+", "terms with no nesting")]
    [DataRow("1px*", "factors with no nesting")]
    [DataRow("(color) and ", "a condition chain")]
    public void ALongRunWithNoDelimiter_IsRefused(string term, string why)
    {
        Assert.IsFalse(CssShape.IsSafe("p{width:calc(" + Times(term, CssShape.MaxRun) + "1px)}"), why);
    }

    [TestMethod]
    public void ACommentCannotResetARun()
    {
        Assert.IsFalse(CssShape.IsSafe("p{width:calc(" + Times("1px+/*;*/", CssShape.MaxRun) + "1px)}"));
    }

    [TestMethod]
    public void AStringCannotResetARun()
    {
        Assert.IsFalse(CssShape.IsSafe("p{width:calc(" + Times("1px+\";\"", CssShape.MaxRun) + "1px)}"));
    }

    [TestMethod]
    public void ManyVariables_AreRefused()
    {
        Assert.IsFalse(CssShape.IsSafe(Times("--a:var(--b);", CssShape.MaxVariables + 1)));
        Assert.IsFalse(CssShape.IsSafe(Times("--a:VAR(--b);", CssShape.MaxVariables + 1)));
        Assert.IsFalse(CssShape.IsSafe(Times("--a:v\\61r(--b);", CssShape.MaxVariables + 1)), "the engine reads the escape as var");
        Assert.IsFalse(CssShape.IsSafe(Times("--a:v\\61 r(--b);", CssShape.MaxVariables + 1)), "with the space that ends an escape");
    }

    [TestMethod]
    public void OrdinaryCss_IsLeftAlone()
    {
        var rules = string.Concat(Enumerable.Range(0, 6000).Select(i =>
            $"/* block ({i}) */ .c{i}:not(.a):hover>td{{color:rgba(1,2,3,.5);font-family:\"Helvetica (Neue)\",'Arial';background:url(x{i}.png) no-repeat}}\n"));

        Assert.IsTrue(CssShape.IsSafe("@import url(\"a.css\");@media only screen and (max-width:480px){" + rules + "}"));
    }

    // Each of these leaves a reading behind that is not the real one. If it outlived the place it
    // belongs to, it would come back a level higher every time and refuse a sheet with nothing wrong.
    [TestMethod]
    [DataRow(".a{/* inside */color:red}/* between */", "a comment inside a rule")]
    [DataRow("/* one *//* two */.a{color:red}", "comments side by side")]
    [DataRow(".a{font-family:\"X\"}@media (max-width:480px){.b{color:red}}", "a string before a bracket")]
    [DataRow("@font-face{src:url(\"a.woff\") format(\"woff\"),local('b')}@media (min-width:1px){p{top:0}}", "quoted urls")]
    [DataRow(".a{background:url('a.png')}@media screen and (max-width:1px){.b{content:\"(\"}}", "a bracket in a string")]
    [DataRow(".a{filter:progid:DXImageTransform.Microsoft.gradient(startColorstr='#80000000',endColorstr='#80000000')}", "an old filter")]
    public void RepetitionAlone_IsNotNesting(string unit, string why)
    {
        Assert.IsTrue(CssShape.IsSafe(Times(unit, 2000)), why);
    }

    [TestMethod]
    public void AValueUpToTheLimit_IsLeftAlone()
    {
        Assert.IsTrue(CssShape.IsSafe("p{font-family:" + new string('a', CssShape.MaxRun - 16) + "}"));
    }

    [TestMethod]
    public void ADataUriInAUrl_IsNotARunOfWords()
    {
        var base64 = Times("iVBORw0KGgo+AAAA/NSUhEUg", 2000);

        Assert.IsTrue(CssShape.IsSafe($".a{{background:url(data:image/png;base64,{base64})}}"));
        Assert.IsTrue(CssShape.IsSafe($".a{{background:url(\"data:image/png;base64,{base64}\")}}"));
    }

    [TestMethod]
    public void BalancedNesting_ComesBackDownAgain()
    {
        Assert.IsTrue(CssShape.IsSafe(Times("p{color:rgb(1,2,3)}")));
        Assert.IsTrue(CssShape.IsSafe(Times("@media screen{p{color:red}}")));
    }

    public static IEnumerable<object[]> ShapesAtTheLimit =>
    [
        [$"<style>{Times("@media screen{", CssShape.MaxNesting)}</style>"],
        [$"<style>{Times("@supports (color:red){", CssShape.MaxNesting / 2)}</style>"],
        [$"<style>{Times("p{&", CssShape.MaxNesting)}</style>"],
        [$"<style>{Times(":has(", CssShape.MaxNesting)}a{{color:red}}</style>"],
        [$"<style>{Times(":not(", CssShape.MaxNesting)}a{{color:red}}</style>"],
        [$"<style>p{{width:{Times("calc(", CssShape.MaxNesting)}1px}}</style>"],
        [$"""<p style="width:{Times("calc(", CssShape.MaxNesting)}1px">x</p>"""],
        [$$"""<style media="{{Times("(", CssShape.MaxNesting)}}">p{color:red}</style>"""],
        [$"<style>p{{width:calc(1px{Times("+1", (CssShape.MaxRun / 2) - 16)})}}</style>"],
        [$"<style>p{{width:calc(1px{Times("*2", (CssShape.MaxRun / 2) - 16)})}}</style>"],
        ["<style>:root{" + string.Concat(Enumerable.Range(0, CssShape.MaxVariables - 2).Select(i => $"--v{i}:calc(var(--v{i + 1}) + 1px);"))
            + $"--v{CssShape.MaxVariables - 2}:1px}}p{{width:var(--v0)}}</style>"],
    ];

    /// <summary>The limits are worth what the engine survives at them, on the stack the conversion's tests use.</summary>
    [TestMethod]
    [DynamicData(nameof(ShapesAtTheLimit))]
    public void WhatPassesTheCheck_DoesNotOverflowTheEngine(string html)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var context = BrowsingContext.New(AngleSharp.Configuration.Default.WithCss());
                var document = new HtmlParser(new HtmlParserOptions(), context).ParseDocument(html + "<p>x</p>");
                var styles = document.DefaultView!.GetStyleCollection(
                    new DefaultRenderDevice { ViewPortWidth = 1024, ViewPortHeight = 768, DeviceWidth = 1024, DeviceHeight = 768 });
                foreach (var element in document.QuerySelectorAll("*"))
                    _ = styles.ComputeDeclarations(element).GetPropertyValue("width");
            }
            catch (Exception e)
            {
                failure = e;
            }
        }, MailBodyBoundsTests.SmallStack);

        thread.Start();
        thread.Join();
        Assert.IsNull(failure, failure?.ToString());
    }

    [TestMethod]
    public void ASheetTheEngineMustNotRead_IsNotQuietlyDropped()
    {
        // A style element swallows its parser's failure and goes on with no rules, which would show
        // the model everything those rules hid.
        var html = $"<style>{Times("@media screen{")}</style><style>.x{{display:none}}</style><p class=\"x\">SECRET</p><p>Parole</p>";

        var (body, format) = MailBody.ForModel(html, MailBody.HtmlFormat);

        Assert.AreEqual(MailBody.OmittedFormat, format);
        Assert.IsFalse(body.Contains("SECRET", StringComparison.Ordinal));
    }
}
