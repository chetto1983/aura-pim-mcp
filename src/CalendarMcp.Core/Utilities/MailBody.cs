using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ReverseMarkdown;

namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Turns an HTML mail body into what a model should read: Markdown that keeps links, lists and
/// tables and drops layout, styling, images, tracking and any text the reader cannot see. Conversion
/// happens where the body is read for the model, so <see cref="Models.EmailMessage.Body"/> keeps
/// the provider's bytes.
/// </summary>
public static partial class MailBody
{
    public const string HtmlFormat = "html";
    public const string MarkdownFormat = "markdown";

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

    private static readonly Config Conversion = BuildConversion();

    /// <summary>The body and its format as the model should receive them.</summary>
    public static (string Body, string Format) ForModel(string body, string format) =>
        format == HtmlFormat ? (ToMarkdown(body), MarkdownFormat) : (body, format);

    public static string ToMarkdown(string html) =>
        string.IsNullOrWhiteSpace(html)
            ? string.Empty
            : BlankLines().Replace(StripInvisibleCharacters(NewConverter().Convert(html)), "\n\n").Trim();

    /// <summary>The same reading as Markdown, with no syntax and no link targets.</summary>
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var document = new HtmlParser().ParseDocument(NewConverter().Preprocess(html));
        var text = new StringBuilder();
        AppendText(document.Body!, text);
        return Whitespace().Replace(StripInvisibleCharacters(text.ToString()), " ").Trim();
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
    private static Config BuildConversion()
    {
        var config = new Config { GithubFlavored = true };
        config.Formatting.OutputLineEnding = "\n";
        config.Links.SmartHref = true;
        foreach (var scheme in AllowedLinkSchemes)
            config.Links.WhitelistedSchemes.Add(scheme);
        config.Tags.Unknown = Config.UnknownTagsOption.Bypass;
        config.Tables.CellListHandling = Config.TableCellListHandlingOption.InlineText;

        // Hidden text goes first: it reads the cascade, which needs the stylesheets still in place.
        config.Preprocess
            .Add("remove hidden text", HiddenText.Remove)
            .RemoveComments()
            .RemoveScripts()
            .Transform("img", ImageToAltText)
            .Transform("a[href]", link => link.SetAttribute("href", WithoutTrackingParameters(link.GetAttribute("href")!)))
            .RemoveStyles()
            .RemoveClasses()
            .RemoveEmptyElements("a, p, div, span, td, tr, tbody, table")
            .Add("flatten layout tables", LayoutTables.Flatten);

        return config;
    }

    // A browsing context is not safe across threads, and AngleSharp's documentation says so.
    // Configuration.Default has no requester, so a <link> or an image is never fetched.
    private static Converter NewConverter() =>
        new(Conversion, BrowsingContext.New(AngleSharp.Configuration.Default.WithCss()));

    private static void ImageToAltText(IElement image)
    {
        var alt = image.GetAttribute("alt");
        if (string.IsNullOrWhiteSpace(alt))
            image.Remove();
        else
            image.Replace(image.Owner!.CreateTextNode(alt));
    }

    private static void AppendText(INode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText words)
            {
                text.Append(words.Data);
            }
            else if (child is IElement element)
            {
                var block = !InlineTags.Contains(element.LocalName);
                if (block) text.Append(' ');
                AppendText(element, text);
                if (block) text.Append(' ');
            }
        }
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
