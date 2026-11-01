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

    public static string Of(string body, string format)
    {
        var text = format == MailBody.HtmlFormat ? MailBody.ToPlainText(body) : body;
        return Cap(Whitespace().Replace(Clean(text), " ").Trim());
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

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)\s]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<?(?:(?:https?|ftp|mailto):|www\.)[^\s>]+>?", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"^[ \t]*(?:>+[ \t]*|#{1,6}[ \t]+|[-*+][ \t]+)", RegexOptions.Multiline)]
    private static partial Regex LineMarker();

    [GeneratedRegex(@"\*\*|__|``?")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
