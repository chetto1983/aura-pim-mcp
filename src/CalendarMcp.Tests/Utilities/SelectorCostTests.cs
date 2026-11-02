using System.Diagnostics;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Html.Parser;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Utilities;

namespace CalendarMcp.Tests.Utilities;

/// <summary>
/// Stylesheets that make selector matching explode. Nothing in .NET can stop a thread that is inside
/// the engine's matching, so each of these runs on a background thread: a hang fails by its timeout
/// and leaves the test host alone.
/// </summary>
[TestClass]
public class SelectorCostTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(6);

    private static string Repeated(string unit, int times) => string.Concat(Enumerable.Repeat(unit, times));

    private static string Deep(string selector) =>
        $"<style>{selector}{{color:red}}</style>{Repeated("<div>", 150)}<p>Parole</p>";

    private static string Flat(string selector) =>
        $"<style>{selector}{{color:red}}</style>{Repeated("<i></i>", 150)}<p>Parole</p>";

    private static string Rows(string selector) =>
        $"<style>{selector}{{color:red}}</style><table>{Repeated("<tr><td>x</td></tr>", 150)}</table>";

    private static T Within<T>(TimeSpan patience, Func<T> convert)
    {
        var result = default(T);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = convert();
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        { IsBackground = true };

        thread.Start();
        Assert.IsTrue(thread.Join(patience), $"still running after {patience}");
        Assert.IsNull(failure, failure?.ToString());
        return result!;
    }

    public static IEnumerable<object[]> StylesheetsThatExplode =>
    [
        ["the review's mail", Deep("z" + Repeated(" *", 20) + " p")],
        ["more combinators than levels", Deep("z" + Repeated(" *", 200) + " p")],
        ["siblings", Flat("z" + Repeated("~*", 20) + "~p")],
        ["siblings at every level", $"<style>z{Repeated(" *~*", 10)} p{{color:red}}</style>{Repeated("<i></i><div>", 60)}<p>Parole</p>"],
        ["table columns", Rows("z" + Repeated("||td", 20))],
        [":is()", Deep(":is(z" + Repeated(" *", 20) + " p)")],
        [":where()", Deep(":where(z" + Repeated(" *", 20) + " p)")],
        [":matches()", Deep(":matches(z" + Repeated(" *", 20) + " p)")],
        [":not()", Deep(":not(z" + Repeated(" *", 20) + " p)")],
        [":has()", Deep("div:has(z" + Repeated(" *", 20) + " p)")],
        ["a compound of ten of them", Deep("z" + Repeated(" :is(* *)", 10) + " p")],
        ["nested :is()", Deep(Repeated(":is(", 20) + "z" + Repeated(" *)", 20) + " p")],
        ["nested :has()", Deep("div" + Repeated(":has(", 12) + "z" + Repeated(")", 12))],
        [":not() around :has()", Deep(":not(div" + Repeated(":has(", 12) + "z" + Repeated(")", 12) + ")")],
        ["an of-clause", Flat(":nth-child(1 of z" + Repeated("~*", 20) + "~p)")],
        ["a list of explosive selectors", Deep("a,b,z" + Repeated(" *", 20) + " p,i")],
        ["rules nested in rules", $"<style>z{Repeated("{*", 20)}{{color:red{Repeated("}", 20)}}}</style>{Repeated("<div>", 150)}<p>Parole</p>"],
        ["an unknown function with a nested rule the engine cannot read", $"<style>z{Repeated(" *", 20)} p:contains(a){{:nosuch{{color:red}}}}</style>{Repeated("<div>", 150)}<p>Parole</p>"],
        ["inside @media",$"<style>@media screen{{z{Repeated(" *", 20)} p{{color:red}}}}</style>{Repeated("<div>", 150)}<p>Parole</p>"],
        ["inside @supports", $"<style>@supports (color:red){{z{Repeated(" *", 20)} p{{color:red}}}}</style>{Repeated("<div>", 150)}<p>Parole</p>"],
        ["inside @layer", $"<style>@layer a{{z{Repeated(" *", 20)} p{{color:red}}}}</style>{Repeated("<div>", 150)}<p>Parole</p>"],
        ["in a second sheet", $"<style>p{{color:red}}</style><style>z{Repeated(" *", 20)} p{{color:red}}</style>{Repeated("<div>", 150)}<p>Parole</p>"],
        ["rules that are only slow together", "<style>" + string.Concat(Enumerable.Range(0, 60).Select(i => $"z{i}{Repeated(" *", 3)} p{{color:red}}")) + $"</style>{Repeated("<div>", 150)}<p>Parole</p>"],
    ];

    [TestMethod]
    [DynamicData(nameof(StylesheetsThatExplode))]
    public void AStylesheetThatExplodes_IsRefusedWhole(string shape, string html)
    {
        var (body, format) = Within(Patience, () => MailBody.ForModel(html, MailBody.HtmlFormat));

        Assert.AreEqual(MailBody.OmittedFormat, format, shape);
        StringAssert.Contains(body, "styles", shape);
        Assert.IsFalse(body.Contains("Parole", StringComparison.Ordinal), shape);
    }

    [TestMethod]
    [DynamicData(nameof(StylesheetsThatExplode))]
    public void AStylesheetThatExplodes_LeavesAnEmptySnippet(string shape, string html)
    {
        var watch = Stopwatch.StartNew();

        Assert.AreEqual(string.Empty, Within(Patience, () => MailSnippet.Of(html, MailBody.HtmlFormat)), shape);
        Assert.IsTrue(watch.Elapsed < MailBody.SnippetBudget, $"{shape}: {watch.Elapsed}");
    }

    /// <summary>What real sheets look like: a few combinators, on trees as deep as nested layout tables get.</summary>
    public static IEnumerable<object[]> OrdinaryStylesheets =>
    [
        ["a chain as long as they get", "body .wrap table tr td .cell p a{color:red}"],
        ["a framework's reset", "article,aside,footer,header,nav,section,main,table td,table th,ul ul,ol ol{margin:0}"],
        ["a list of two hundred rules", string.Concat(Enumerable.Range(0, 200).Select(i => $".a{i} .b td a{{color:red}}"))],
        ["siblings and children", "ul>li~li>a:hover{color:red}p+p{margin:0}.a~.b{color:red}"],
        [":is() and :not()", ":is(h1,h2,h3) a:not(.x):not(.y){color:red}:where(table td) p{margin:0}"],
        [":has()", "td:has(> img) p{margin:0}"],
        ["nth-child", "tr:nth-child(2n+1) td:nth-of-type(2) a{color:red}"],
        ["outlook's and gmail's hacks", "u + .body .gmail{color:red}#outlook a{padding:0}div > u + .body .x{color:red}[owa] .y{color:red}"],
    ];

    [TestMethod]
    [DynamicData(nameof(OrdinaryStylesheets))]
    public void AnOrdinaryStylesheet_OnADeepLayout_IsStillRead(string shape, string css)
    {
        var html = $"<style>{css}</style>{Repeated("<table><tr><td>", 12)}<p>Parole</p>";

        var (body, format) = MailBody.ForModel(html, MailBody.HtmlFormat);

        Assert.AreEqual(MailBody.MarkdownFormat, format, $"{shape}: {body}");
        StringAssert.Contains(body, "Parole", shape);
    }

    /// <summary>Far enough under that a much larger newsletter of the same kind stays under too.</summary>
    [TestMethod]
    public void TheNewsletter_IsNowhereNearTheBound()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Mail", "newsletter-large.html"));

        Assert.IsTrue(StepsOf(html) < SelectorCost.MaxSteps / 100, $"{StepsOf(html)}");
    }

    [TestMethod]
    [DataRow(10, 3, 1 + 10 + 45 + 120)]
    [DataRow(4, 9, 16)]
    [DataRow(3, 0, 1)]
    [DataRow(0, 5, 1)]
    public void Choices_CountEveryShorterPickToo(int from, int picks, int expected)
    {
        Assert.AreEqual(expected, SelectorCost.Choices(from, picks));
    }

    /// <summary>More combinators than levels must not read as no way through: that was all the ways there are.</summary>
    [TestMethod]
    public void MoreCombinatorsThanLevels_CostsTheMost()
    {
        Assert.IsTrue(double.IsInfinity(SelectorCost.Choices(5000, 5000)) || SelectorCost.Choices(5000, 5000) > 1e300);
    }

    [TestMethod]
    public void TheEnginesOwnSheet_CountsForNothing()
    {
        Assert.AreEqual(0, StepsOf($"{Repeated("<div>", 200)}<ul><li><ol><li><p>Parole</p>"));
    }

    private static double StepsOf(string html)
    {
        var context = BrowsingContext.New(AngleSharp.Configuration.Default.WithCss());
        var document = new HtmlParser(new HtmlParserOptions(), context).ParseDocument(html);
        var styles = document.DefaultView!.GetStyleCollection(new DefaultRenderDevice { ViewPortWidth = 1024, ViewPortHeight = 768 });

        return SelectorCost.Steps(document.DocumentElement, styles, new ConversionBudget(TimeSpan.FromSeconds(5)));
    }
}
