using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ReverseMarkdown;
using ReverseMarkdown.Readers;

namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Turns an HTML mail body into what a model should read: Markdown that keeps links, lists and
/// tables and drops layout, styling, images, tracking and the text <see cref="HiddenText"/> finds a
/// reader cannot see. Conversion happens where the body is read for the model, so
/// <see cref="Models.EmailMessage.Body"/> keeps the provider's bytes.
/// </summary>
public static partial class MailBody
{
    public const string HtmlFormat = "html";
    public const string MarkdownFormat = "markdown";

    /// <summary>The body could not be converted, and <c>body</c> says so instead of holding the mail.</summary>
    public const string OmittedFormat = "omitted";

    /// <summary>
    /// The most text converted, in characters. Over this a mail has no snippet and the tool says so.
    /// Every provider's body comes through here, so no provider needs a cap of its own.
    /// </summary>
    public const int MaxBodyLength = 1024 * 1024;

    /// <summary>
    /// The deepest tree converted, counting the root as one. Mail layouts stay under a hundred, and the
    /// Markdown reader recurses four frames a level, so this also leaves it most of any thread's stack.
    /// </summary>
    internal const int MaxDepth = 256;

    private const string NotConverted =
        "The body of this mail could not be converted to text, so it is left out. The other fields are unaffected.";

    private const string TooLarge =
        "The body of this mail is too large to convert, over 1 MiB, so it is left out. The other fields are unaffected.";

    internal const string TooDeep =
        "The body of this mail is nested too deeply to read, so it is left out. The other fields are unaffected.";

    /// <summary>A snippet is one of a page of them, and the classifier reading it can do without.</summary>
    internal static readonly TimeSpan SnippetBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>A model has asked for this one mail, so it gets the longer wait.</summary>
    internal static readonly TimeSpan DetailsBudget = TimeSpan.FromSeconds(3);

    private const string WarmUpMail = """
        <style>.a{color:#000;font-size:12px}@media (max-width:600px){.b{display:none}}</style>
        <table><tr><td class="a" style="width:100%"><a href="https://example.com/">a</a><img alt="b" src="c"></td></tr></table>
        """;

    private static readonly Lazy<string> WarmedUp = new(() => ToPlainText(WarmUpMail, new ConversionBudget(TimeSpan.FromDays(1))));

    private static readonly string[] AllowedLinkSchemes = ["http", "https", "mailto"];

    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "yclid", "igshid", "twclid", "ttclid",
        "mc_cid", "mc_eid", "mkt_tok", "_hsenc", "_hsmi", "hsctatracking", "vero_id", "vero_conv",
        "oly_anon_id", "oly_enc_id", "_ga", "_gl"
    };

    private static readonly HashSet<string> InlineTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "bdi", "bdo", "cite", "code", "em", "font", "i", "kbd", "mark", "q",
        "s", "small", "span", "strike", "strong", "sub", "sup", "u", "var"
    };

    /// <summary>
    /// The body and its format as the model should receive them. HTML that cannot be converted is
    /// never handed over as it is: what the filter would have removed is exactly what must not arrive.
    /// </summary>
    public static (string Body, string Format) ForModel(string body, string format)
    {
        if (format != HtmlFormat)
            return (body, format);

        try
        {
            return (ToMarkdown(body), MarkdownFormat);
        }
        catch (UnconvertibleMailException refused)
        {
            return (refused.Message, OmittedFormat);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (NotConverted, OmittedFormat);
        }
    }

    public static string ToMarkdown(string html)
    {
        WarmUp();
        return ToMarkdown(html, new ConversionBudget(DetailsBudget));
    }

    /// <summary>The same reading as Markdown, with no syntax and no link targets.</summary>
    public static string ToPlainText(string html)
    {
        WarmUp();
        return ToPlainText(html, new ConversionBudget(SnippetBudget));
    }

    /// <summary>
    /// The first conversion in a process compiles the parser and the cascade, measured at 360 ms to
    /// over 500 on a 62 KB mail, so it would spend a snippet's budget on work no mail asked for and
    /// lose the first snippet after every start. A budget starts after this.
    /// </summary>
    internal static void WarmUp() => _ = WarmedUp.Value;

    // Not Converter.Convert: its Cleaner runs regexes over the raw text that are quadratic on a
    // flood of <script>, before anything can observe a budget, and its parser takes no options.
    // Its steps are public, and the only one left out strips an unclosed <script> or <style> so
    // that the text behind it survives, which is not what a person reading the mail would see.
    internal static string ToMarkdown(string html, ConversionBudget budget)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var conversion = BuildConversion(budget);
        var document = Prepared(html, conversion, budget);
        var markdown = new Converter(conversion).Render(new MarkdownDomReader(conversion).Read(document.Body!));

        return BlankLines().Replace(StripInvisibleCharacters(markdown), "\n\n").Trim();
    }

    internal static string ToPlainText(string html, ConversionBudget budget)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var document = Prepared(html, BuildConversion(budget), budget);

        return Whitespace().Replace(StripInvisibleCharacters(TextOf(document.Body!, budget)), " ").Trim();
    }

    /// <summary>
    /// The parsed document with the conversion's steps applied. The budget goes into the parser as
    /// well as the steps: the tree's depth is limited but its stack of open elements is not, and
    /// start tags walk that stack, so a megabyte of <c>&lt;i&gt;</c> parses for longer than a request lives.
    /// </summary>
    private static IDocument Prepared(string html, Config conversion, ConversionBudget budget)
    {
        if (html.Length > MaxBodyLength)
            throw new UnconvertibleMailException(TooLarge);

        // A browsing context is not safe across threads, and AngleSharp's documentation says so.
        // Configuration.Default has no requester, so a <link> or an image is never fetched.
        var context = BrowsingContext.New(AngleSharp.Configuration.Default
            .With<ICssParser>(browsing => new GuardedCssParser(new CssParser(default, browsing)))
            .WithCss());

        var options = new HtmlParserOptions
        {
            MaximumDepth = MaxDepth,
            OnCreated = (_, _) =>
            {
                budget.Check();

                // A <pre> reads the next token inside its own frame (HtmlDomBuilder.PreventNewLine),
                // so a megabyte of them recurses and no limit on the tree stops it.
                if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
                    throw new UnconvertibleMailException(TooDeep);
            }
        };
        var document = new HtmlParser(options, context).ParseDocument(html.ReplaceLineEndings("\n").TrimEnd());
        ((GuardedCssParser)context.GetService<ICssParser>()!).ThrowIfRefused();

        conversion.Preprocess.Apply(document.DocumentElement!);
        return document;
    }

    public static string WithoutTrackingParameters(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || uri.Query.Length < 2)
            return href;

        var parameters = uri.Query[1..].Split('&');
        var kept = parameters.Where(p => !IsTrackingParameter(p)).ToList();
        if (kept.Count == parameters.Length)
            return href;

        var query = kept.Count == 0 ? string.Empty : "?" + string.Join('&', kept);
        return uri.GetLeftPart(UriPartial.Path) + query + uri.Fragment;
    }

    private static bool IsTrackingParameter(string parameter)
    {
        var name = parameter.Split('=', 2)[0];
        return name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingParameters.Contains(name);
    }

    // The default flavor, not MarkdownFlavor.GitHub: measured on a 62,890-character newsletter,
    // that flavor returned the whole document unchanged, because it passes HTML blocks through
    // verbatim (docs/preprocessing.md, "Notes", in mysticmind/reversemarkdown-net). The default
    // flavor renders GFM tables anyway (docs/configuration.md, "GithubFlavored").
    private static Config BuildConversion(ConversionBudget budget)
    {
        var config = new Config { GithubFlavored = true };
        config.Formatting.OutputLineEnding = "\n";
        config.Links.SmartHref = true;
        foreach (var scheme in AllowedLinkSchemes)
            config.Links.WhitelistedSchemes.Add(scheme);
        config.Tags.Unknown = Config.UnknownTagsOption.Bypass;
        config.Tables.CellListHandling = Config.TableCellListHandlingOption.InlineText;

        // Hidden text goes first: it reads the cascade, which needs the stylesheets still in place.
        // The library's own steps cannot observe a budget, so it is read between them.
        config.Preprocess
            .Add("remove hidden text", root => HiddenText.Remove(root, budget))
            .RemoveComments()
            .RemoveScripts()
            .Add("stay in budget", _ => budget.Check())
            .Transform("img", ImageToAltText)
            .Transform("a[href]", link => link.SetAttribute("href", WithoutTrackingParameters(link.GetAttribute("href")!)))
            .Add("stay in budget", _ => budget.Check())
            .RemoveStyles()
            .RemoveClasses()
            .Add("stay in budget", _ => budget.Check())
            .RemoveEmptyElements("a, p, div, span, td, tr, tbody, table")
            .Add("stay in budget", _ => budget.Check())
            .Add("flatten layout tables", root => LayoutTables.Flatten(root, budget));

        return config;
    }

    private static void ImageToAltText(IElement image)
    {
        var alt = image.GetAttribute("alt");
        if (string.IsNullOrWhiteSpace(alt))
            image.Remove();
        else
            image.Replace(image.Owner!.CreateTextNode(alt));
    }

    private static string TextOf(IElement root, ConversionBudget budget)
    {
        var text = new StringBuilder();
        var pending = new Stack<INode?>();
        PushChildren(root, pending);

        // A null stands for the end of a block, which keeps its words from meeting the next ones.
        while (pending.TryPop(out var node))
        {
            budget.Check();

            if (node is null)
            {
                text.Append(' ');
            }
            else if (node is IText words)
            {
                text.Append(words.Data);
            }
            else if (node is IElement element)
            {
                if (!InlineTags.Contains(element.LocalName))
                {
                    text.Append(' ');
                    pending.Push(null);
                }

                PushChildren(element, pending);
            }
        }

        return text.ToString();
    }

    private static void PushChildren(INode node, Stack<INode?> pending)
    {
        var children = node.ChildNodes;
        for (var i = children.Length - 1; i >= 0; i--)
            pending.Push(children[i]);
    }

    internal static string StripInvisibleCharacters(string text) => InvisibleCharacters().Replace(text, string.Empty);

    // Format characters and the Unicode tag block, which render as nothing and still reach a model.
    [GeneratedRegex(@"\p{Cf}|\uDB40[\uDC00-\uDC7F]")]
    private static partial Regex InvisibleCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)+")]
    private static partial Regex BlankLines();
}
