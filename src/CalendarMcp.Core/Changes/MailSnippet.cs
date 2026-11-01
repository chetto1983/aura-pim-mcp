using System.Text.RegularExpressions;
using CalendarMcp.Core.Utilities;

namespace CalendarMcp.Core.Changes;

/// <summary>
/// The first words of a mail, as a rule or a classifier reads them: plain text with no markup and no
/// URLs. Provider previews are not used: they know nothing of text hidden from the reader.
/// </summary>
internal static partial class MailSnippet
{
    /// <summary>
    /// 400 characters hold a greeting and the first sentences, which is what a triage classifier
    /// reads; the rest of a mail is one <c>/admin/items</c> or <c>get_email_details</c> away.
    /// </summary>
    public const int MaxLength = 400;

    /// <summary>
    /// The snippet, or empty when the body cannot be read. The mail still arrives: a page that fails
    /// is replayed forever, so one mail would stop every mail behind it. The snippet is for a
    /// classifier, and the verdict does not read it.
    /// </summary>
    public static string Of(string body, string format)
    {
        MailBody.WarmUp();
        return Of(() => (body, format), new ConversionBudget(MailBody.SnippetBudget));
    }

    public static string Of(Func<(string Body, string Format)> read, ConversionBudget budget)
    {
        try
        {
            var (body, format) = read();
            if (body.Length > MailBody.MaxBodyLength)
                return string.Empty;

            var text = format == MailBody.HtmlFormat ? MailBody.ToPlainText(body, budget) : body;
            return Cap(Whitespace().Replace(Clean(text), " ").Trim());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string Clean(string text)
    {
        var withoutLinks = BareUrl().Replace(MarkdownLink().Replace(text, "$1"), " ");
        var withoutMarkers = LineMarker().Replace(withoutLinks, string.Empty);
        return Emphasis().Replace(MailBody.StripInvisibleCharacters(withoutMarkers), string.Empty);
    }

    private static string Cap(string text)
    {
        if (text.Length <= MaxLength)
            return text;

        var cut = text.LastIndexOf(' ', MaxLength);
        var end = cut > MaxLength / 2 ? cut : MaxLength;
        if (char.IsHighSurrogate(text[end - 1]))
            end--;

        return text[..end].TrimEnd();
    }

    // NonBacktracking throughout: every text here is the sender's, and an unclosed [ made the first of
    // these quadratic, 17 s on a megabyte of them.
    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)\s]*\)", RegexOptions.NonBacktracking)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<?(?:(?:https?|ftp|mailto):|www\.)[^\s>]+>?", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"^[ \t]*(?:>+[ \t]*|#{1,6}[ \t]+|[-*+][ \t]+)", RegexOptions.Multiline | RegexOptions.NonBacktracking)]
    private static partial Regex LineMarker();

    [GeneratedRegex(@"\*\*|__|``?", RegexOptions.NonBacktracking)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s+", RegexOptions.NonBacktracking)]
    private static partial Regex Whitespace();
}
