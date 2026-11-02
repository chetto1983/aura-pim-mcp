namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Whether HTML is safe to hand to AngleSharp's parser, read from the raw text before it starts. The
/// count is of what is in the text, so a comment or an attribute can only make it stricter.
/// </summary>
internal static class MarkupShape
{
    /// <summary>
    /// The most of the units the tokenizer calls itself on. Its states call each other in place of
    /// looping on <c>&lt;/&gt;</c> and on <c>--!</c> in a comment, and only the JIT's tail calls save the
    /// stack once it has warmed up. On cold code 80,000 units overflow 8 MiB, which no thread survives.
    /// This many take a fortieth of that, and a mail has a handful.
    /// </summary>
    internal const int MaxRecursion = 1024;

    public static bool IsSafe(string html)
    {
        var recursion = 0;

        for (var i = 0; i + 2 < html.Length; i++)
        {
            if (html[i] == '<')
            {
                if (html[i + 1] == '/' && html[i + 2] == '>')
                    recursion++;
            }
            else if (html[i] == '-' && html[i + 1] == '-' && html[i + 2] == '!')
            {
                recursion++;
            }
        }

        return recursion <= MaxRecursion;
    }
}
