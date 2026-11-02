namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Whether HTML is safe to hand to AngleSharp's parser, read from the raw text before it starts. Once it
/// has, the budget is read only when an element is created, and a token that creates none reads nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every count here is of what is in the text, with no idea of what is a tag. A real tag is always in
/// the count, so text in a comment, an attribute or a style element can only make the check stricter.
/// Telling real tags from false ones would take a tokenizer, and a false closing tag in a comment
/// would then be the way to fool a count of what stays open.
/// </para>
/// <para>
/// The same is why ordinary mail can be caught: this cannot see that a table closes what it opens. A
/// bare table of a hundred thousand tags scores like a flood of the same size, and is refused above
/// about 320 KB. A table with any styling carries fewer tags per byte and is under the bound at the cap.
/// </para>
/// </remarks>
internal static class MarkupShape
{
    /// <summary>
    /// The most that start tags times tokens may come to. An end tag walks the open elements
    /// (<c>InBodyEndTagAnythingElse</c>, <c>IsInScope</c>) and a text token does too, in
    /// <c>ReconstructFormatting</c>, so the work is what stays open times what walks it, and neither
    /// creates an element. Measured, a step is 5 to 8 ns: 20,000 open and 40,000 end tags took 5 s, and
    /// the review's 100,000 and 185,000 ran past 90 s. So this is about 10 s of one thread at the very
    /// worst, which ends by itself and which no caller waits for. On half a CPU a mail at the bound,
    /// read on both paths, took 48 s to let go of both its threads. It keeps a 1 MiB report of styled
    /// rows (1.4 × 10⁹), 1 MiB of paragraphs (1.9 × 10⁹) and the newsletter 16 times over (1.1 × 10⁸).
    /// </summary>
    internal const long MaxWalk = 2_000_000_000;

    /// <summary>
    /// The most of the units the tokenizer calls itself on. Its states call each other in place of
    /// looping on <c>&lt;/&gt;</c> and on <c>--!</c> in a comment, and only the JIT's tail calls save the
    /// stack once it has warmed up. On the coldest code about 43,500 of either overflow 8 MiB, which no
    /// thread survives. This many take a fortieth of that, and a mail has a handful.
    /// </summary>
    internal const int MaxRecursion = 1024;

    /// <summary>
    /// The most attributes one start tag may have. <c>EmitTag</c> removes the duplicates from a list one
    /// at a time, and each removal moves everything after it, so a tag is quadratic in its attributes:
    /// measured, 100 KB of them took 0.36 s, 520 KB 8 s and 1 MiB 33 s, where a tag of any length with
    /// one attribute costs nothing. So it is attributes that are counted and not length, since a large
    /// inline style, a long <c>href</c> and a <c>data:</c> URI are each one. A real tag has a dozen.
    /// </summary>
    internal const int MaxAttributes = 16_384;

    private enum Part { Outside, Name, Gap, Equals, Bare, Quoted }

    public static bool IsSafe(string html)
    {
        var (startTags, tokens) = (0L, 0L);
        var recursion = 0;
        var (part, quote, attributes) = (Part.Outside, '\0', 0);

        for (var i = 0; i + 2 < html.Length; i++)
        {
            var c = html[i];

            if (c == '<')
            {
                tokens++;

                if (char.IsAsciiLetter(html[i + 1]))
                {
                    startTags++;
                    if (part == Part.Outside)
                        (part, attributes) = (Part.Name, 0);
                }
                else if (html[i + 1] == '/' && html[i + 2] == '>')
                    recursion++;
            }
            else if (c == '-' && html[i + 1] == '-' && html[i + 2] == '!')
            {
                recursion++;
            }

            // The tokenizer's own attribute states, so a > inside a value does not end the tag it is in.
            // Text it reads as a tag and the tokenizer does not (in a script, say) only makes this stricter.
            switch (part)
            {
                case Part.Name when c == '>':
                case Part.Gap when c == '>':
                case Part.Equals when c == '>':
                case Part.Bare when c == '>':
                    part = Part.Outside;
                    break;
                case Part.Name when IsSpace(c) || c == '/':
                case Part.Bare when IsSpace(c):
                    part = Part.Gap;
                    break;
                case Part.Name when c == '=':
                    part = Part.Equals;
                    break;
                case Part.Gap when !IsSpace(c) && c != '/':
                    part = Part.Name;
                    if (++attributes > MaxAttributes)
                        return false;
                    break;
                case Part.Equals when c is '"' or '\'':
                    (part, quote) = (Part.Quoted, c);
                    break;
                case Part.Equals when !IsSpace(c):
                    part = Part.Bare;
                    break;
                case Part.Quoted when c == quote:
                    part = Part.Gap;
                    break;
            }
        }

        return startTags * tokens <= MaxWalk && recursion <= MaxRecursion;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';
}
