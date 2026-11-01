namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Which parts of a mail could be its body, best first, for any provider's tree of parts. text/plain
/// is only preferred where the sender offers it as another form of the same thing, which is what
/// multipart/alternative means. In multipart/mixed the parts are different things in order
/// (RFC 2046 section 5.1.3), and a plain part after the HTML is a list's footer and not the mail.
/// </summary>
internal static class MailParts
{
    /// <summary>Mail nests a few levels. Past this a tree is not carrying a body.</summary>
    private const int MaxNesting = 32;

    private const string PlainText = "text/plain";
    private const string Html = "text/html";
    private const string Alternative = "multipart/alternative";
    private const string Multipart = "multipart/";

    /// <summary>How to read one provider's parts. An attachment is not the body, whatever its type.</summary>
    public sealed record Shape<T>(Func<T, string?> Type, Func<T, bool> IsAttachment, Func<T, IEnumerable<T>> Children);

    public static IEnumerable<T> BodyCandidates<T>(T root, Shape<T> shape) => Walk(root, shape, 0);

    public static string FormatOf(string? type) => Is(type, Html) ? MailBody.HtmlFormat : "text";

    /// <summary>No words a person could read. A part like this is the same as no part.</summary>
    public static bool IsBlank(string text) => string.IsNullOrWhiteSpace(MailBody.StripInvisibleCharacters(text));

    private static IEnumerable<T> Walk<T>(T part, Shape<T> shape, int depth)
    {
        if (depth > MaxNesting || shape.IsAttachment(part))
            yield break;

        var type = shape.Type(part);
        if (Is(type, PlainText) || Is(type, Html))
        {
            yield return part;
            yield break;
        }

        // A forwarded mail is message/rfc822, which stops here: it is its own mail and not this one's body.
        if (type?.StartsWith(Multipart, StringComparison.OrdinalIgnoreCase) != true)
            yield break;

        var children = shape.Children(part);
        if (Is(type, Alternative))
            children = children.OrderBy(child => !Is(shape.Type(child), PlainText));

        foreach (var child in children)
        {
            foreach (var candidate in Walk(child, shape, depth + 1))
                yield return candidate;
        }
    }

    private static bool Is(string? type, string expected) => string.Equals(type, expected, StringComparison.OrdinalIgnoreCase);
}
