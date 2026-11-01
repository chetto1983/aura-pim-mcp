namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Whether CSS is safe to hand to AngleSharp.Css, which recurses once per level with no limit of its
/// own and has no way to be interrupted. Measured on a 1 MiB stack in 1.3.0, the process aborts
/// between 1,000 and 2,000 nested at-rules, 2,000 and 4,000 nested functions or chained variables, and
/// 4,000 and 8,000 terms in one <c>calc()</c>. A long condition chain takes seconds instead.
/// </summary>
/// <remarks>
/// A bracket inside a comment or a string closes nothing, so <c>calc(/*)*/</c> repeated would pass a
/// plain count. Instead this follows every state the tokenizer could be in, and keeps the worst figure
/// for each. It can overstate what the engine will see and it cannot understate it, however the text
/// tries to make the two disagree about where a string or a url ends. A reading that is not the real
/// one has to end where the real one would, or ordinary sheets would climb a level at a time and be
/// refused, which is why a comment closes at its first <c>*/</c> and a string only leads back into a
/// url if a url opened it. See the url states of CssTokenizer and <c>ContentFrom</c>, which reads a
/// value's text a second time and lets a line break through a string, and
/// https://www.w3.org/TR/css-syntax-3/#tokenization.
/// </remarks>
internal static class CssShape
{
    /// <summary>Ordinary CSS nests a handful of levels. This is a tenth of what overflows a 1 MiB stack.</summary>
    public const int MaxNesting = 128;

    /// <summary>The longest selector, prelude or value. Half of what overflows a 1 MiB stack, at two characters a term.</summary>
    public const int MaxRun = 4096;

    /// <summary>
    /// Substituting one variable into another is recursion too. This limit is per text, and a mail can
    /// have any number of them, so what variables grow into is <see cref="HiddenText.MaxPasted"/>'s to stop.
    /// </summary>
    public const int MaxVariables = 256;

    private const int Unreached = -1;

    private enum State { Plain, Single, Double, Comment, UrlStart, UrlSingle, UrlDouble, Url, Count }

    /// <summary>How deep and how long a state can have got. Merging keeps the worst of both.</summary>
    private readonly record struct Reach(int Depth, int Run)
    {
        public static readonly Reach Nowhere = new(Unreached, 0);

        public bool IsReached => Depth != Unreached;

        public bool IsTooMuch => Depth > MaxNesting || Run > MaxRun;

        public Reach Merge(Reach other) => new(Math.Max(Depth, other.Depth), Math.Max(Run, other.Run));
    }

    public static bool IsSafe(string css)
    {
        // Text this short cannot reach any of the limits.
        if (css.Length <= MaxNesting)
            return true;

        Span<Reach> now = stackalloc Reach[(int)State.Count];
        Span<Reach> next = stackalloc Reach[(int)State.Count];
        Span<bool> escaped = stackalloc bool[(int)State.Count];
        now.Fill(Reach.Nowhere);
        now[(int)State.Plain] = new Reach(0, 0);

        var variables = 0;
        var escapeInWord = false;

        for (var i = 0; i < css.Length; i++)
        {
            var c = css[i];
            next.Fill(Reach.Nowhere);

            for (var state = State.Plain; state < State.Count; state++)
            {
                var reach = now[(int)state];
                if (!reach.IsReached)
                    continue;

                // An escape only hides this character from one path, and another may have arrived in
                // this state on the same character, so both readings go on.
                if (escaped[(int)state])
                {
                    escaped[(int)state] = false;
                    Arrive(next, state, reach);
                }

                switch (state)
                {
                    case State.Plain:
                        Plain(css, i, reach, next, escaped);
                        break;
                    case State.Single:
                    case State.Double:
                    case State.UrlSingle:
                    case State.UrlDouble:
                        InString(c, state, reach, next, escaped);
                        break;
                    case State.Comment:
                        InComment(css, i, reach, next);
                        break;
                    case State.UrlStart:
                        AtUrlStart(c, reach, next, escaped);
                        break;
                    case State.Url:
                        InUrl(c, reach, next, escaped);
                        break;
                }
            }

            if (c == '\\')
                escapeInWord = true;
            else if (c == '(')
            {
                if (escapeInWord || FollowsWord(css, i, "var"))
                    variables++;
                escapeInWord = false;
            }
            else if (!IsWordCharacter(c) && !char.IsWhiteSpace(c))
                escapeInWord = false;

            if (variables > MaxVariables)
                return false;

            foreach (var reach in next)
            {
                if (reach.IsTooMuch)
                    return false;
            }

            next.CopyTo(now);
        }

        return true;
    }

    private static void Plain(string css, int i, Reach reach, Span<Reach> next, Span<bool> escaped)
    {
        var (depth, run) = reach;
        var c = css[i];

        switch (c)
        {
            case '\\':
                escaped[(int)State.Plain] = true;
                Arrive(next, State.Plain, reach with { Run = run + 1 });
                break;
            case '"':
                Arrive(next, State.Double, reach);
                break;
            case '\'':
                Arrive(next, State.Single, reach);
                break;
            case '/' when i + 1 < css.Length && css[i + 1] == '*':
                Arrive(next, State.Comment, reach);
                break;
            case '{':
                Arrive(next, State.Plain, new Reach(depth + 1, 0));
                break;
            case '}':
                Arrive(next, State.Plain, new Reach(Math.Max(depth - 1, 0), 0));
                break;
            case ';':
                Arrive(next, State.Plain, reach with { Run = 0 });
                break;
            case '(':
                Arrive(next, State.UrlStart, reach);
                if (!OpensAPlainUrl(css, i))
                    Arrive(next, State.Plain, new Reach(depth + 1, run + 1));
                break;
            case '[':
                Arrive(next, State.Plain, new Reach(depth + 1, run + 1));
                break;
            case ')' or ']':
                Arrive(next, State.Plain, new Reach(Math.Max(depth - 1, 0), run + 1));
                break;
            default:
                Arrive(next, State.Plain, reach with { Run = run + 1 });
                break;
        }
    }

    private static void InString(char c, State state, Reach reach, Span<Reach> next, Span<bool> escaped)
    {
        var quote = state is State.Single or State.UrlSingle ? '\'' : '"';

        if (c == '\\')
            escaped[(int)state] = true;

        if (c == quote || c is '\n' or '\r' or '\f')
        {
            Arrive(next, State.Plain, reach);

            // A url that goes bad after its quote reads on to the next bracket, past any quote.
            if (state is State.UrlSingle or State.UrlDouble)
                Arrive(next, State.Url, reach);
        }

        if (c != quote)
            Arrive(next, state, reach);
    }

    private static void InComment(string css, int i, Reach reach, Span<Reach> next)
    {
        var closes = css[i] == '/' && i > 0 && css[i - 1] == '*';
        if (closes)
            Arrive(next, State.Plain, reach);

        // In /*/ the star belongs to the opening, for a reading that came in on that slash.
        if (!closes || (i > 1 && css[i - 2] == '/'))
            Arrive(next, State.Comment, reach);
    }

    private static void AtUrlStart(char c, Reach reach, Span<Reach> next, Span<bool> escaped)
    {
        if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            Arrive(next, State.UrlStart, reach);
        else if (c == '"')
            Arrive(next, State.UrlDouble, reach);
        else if (c == '\'')
            Arrive(next, State.UrlSingle, reach);
        else
            InUrl(c, reach, next, escaped);
    }

    // A quote is ordinary here: the tokenizer's bad-url state reads on to the bracket, past strings.
    private static void InUrl(char c, Reach reach, Span<Reach> next, Span<bool> escaped)
    {
        if (c == '\\')
            escaped[(int)State.Url] = true;

        Arrive(next, c == ')' ? State.Plain : State.Url, reach);
    }

    private static void Arrive(Span<Reach> next, State state, Reach reach) =>
        next[(int)state] = next[(int)state].Merge(reach);

    // A url after a separator is a url and nothing else, which keeps a data URI from reading as a run
    // of words. Anything less clear in front of it goes both ways: #url( is a hash and a bracket.
    private static bool OpensAPlainUrl(string css, int bracket) =>
        FollowsWord(css, bracket, "url")
        && (bracket == 3 || css[bracket - 4] is ':' or ',' or '(' or ' ' or '\t' or '\n');

    private static bool FollowsWord(string css, int bracket, string word) =>
        bracket >= word.Length && css.AsSpan(bracket - word.Length, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsWordCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c > '\u007f';
}
