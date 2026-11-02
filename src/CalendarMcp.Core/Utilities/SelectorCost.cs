using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;

namespace CalendarMcp.Core.Utilities;

/// <summary>
/// What selector matching can cost on this document, worked out before the engine is asked.
/// <c>ComplexSelector.MatchCascade</c> tries every candidate at every combinator with no memo and reads
/// no token and no clock, so <c>z * * * p</c> over 150 levels has about 10²⁴ paths when <c>z</c> never
/// matches. <see cref="ConversionBudget"/> is read between elements and never inside one, and the rule
/// index keys on <c>p</c>, so nothing else sees it coming.
/// </summary>
/// <remarks>
/// <para>
/// Every compound is taken to match, because the one that fails can be the leftmost. That overstates an
/// ordinary sheet by a long way, which is what <see cref="MaxSteps"/> leaves room for.
/// </para>
/// <para>
/// What each part costs in this engine, read in AngleSharp 1.9.0:
/// <list type="bullet">
/// <item>A descendant combinator walks the ancestors, so k of them choose up to k of the d above an
/// element, in order, and every shorter choice is visited on the way: the sum of C(d, j) for j up to k.
/// k can pass d, which is why it is the sum and not C(d, k), which would be zero.</item>
/// <item>A <c>~</c> does the same over the siblings before an element, and a run of them is a choice
/// in one list. Runs apart from each other are at different levels, so they multiply.</item>
/// <item><c>||</c> yields every cell of a column, itself included, so nothing narrows it down.</item>
/// <item><c>&gt;</c>, <c>+</c>, <c>&gt;&gt;&gt;</c> and <c>|</c> reach one element at most.</item>
/// <item><c>:is()</c>, <c>:where()</c>, <c>:matches()</c> and <c>:not()</c> run their argument once per
/// candidate, so they multiply what reaches them. <c>:not()</c> makes failing as good as matching.</item>
/// <item><c>:has()</c> does the same for every element it walks, and it can walk the whole tree.</item>
/// <item><c>:nth-child(n of S)</c> and its kin run S on every sibling.</item>
/// <item>A rule inside a rule climbs the ancestors for its parent (<c>CssStyleRule.TryMatch</c>), which
/// is one more descendant combinator that no selector shows.</item>
/// </list>
/// </para>
/// <para>
/// The functions are closures in the engine, and all a visitor gets is their text, so that text goes
/// back through the engine's own selector parser instead of a second tokenizer that could be fooled. A
/// function this does not know, and text that parser will not take, is refused rather than guessed at.
/// </para>
/// </remarks>
internal static class SelectorCost
{
    /// <summary>
    /// The steps one element may cost, and the budget is read again after it. The estimate tracks the
    /// engine closely: measured from 7.6 × 10⁷ to 1.7 × 10¹⁰ steps, a step is 4 to 5 ns warm on ancestors,
    /// 10 on siblings and 25 on columns, and about 100 and 370 on code held at the coldest tier. So this
    /// is 1.3 s warm, or 6 s on columns, and an element that needs <c>var()</c> pays that for each ancestor
    /// that matches as much. That is a thread that ends by itself, which is all this has to promise: the
    /// time is the budget's, and <see cref="ConversionThread"/> does not wait for it. Real sheets are a
    /// long way under: the newsletter estimates 10 and sixteen of them in one mail 160, and a chain of
    /// seven on 51 levels, deeper than layout tables go, 1.4 × 10⁸. The review's mail estimates 4 × 10²⁵.
    /// </summary>
    internal const double MaxSteps = 2.5e8;

    /// <summary>Refuses a mail whose rules could cost more than <see cref="MaxSteps"/> on one element.</summary>
    /// <exception cref="UnconvertibleMailException">They could.</exception>
    public static void Check(IElement root, IStyleCollection styles, ConversionBudget budget)
    {
        // Written so that a NaN refuses: it is false against every comparison.
        if (!(Steps(root, styles, budget) <= MaxSteps))
            throw new UnconvertibleMailException(MailBody.TooIntricate);
    }

    /// <summary>The most steps the mail's own rules can take on one element of this document.</summary>
    internal static double Steps(IElement root, IStyleCollection styles, ConversionBudget budget)
    {
        // The engine's own sheet is fixed, and a mail's cost is what the mail brings.
        var rules = styles.Where(rule => rule.Owner?.OwnerNode is not null).ToList();
        if (rules.Count == 0)
            return 0;

        var estimate = new Estimate(TreeShape.Of(root, budget), root.Owner!.Context.GetService<ICssSelectorParser>()!, budget);

        var steps = 0d;
        foreach (var rule in rules)
        {
            budget.Check();
            steps += estimate.OfRule(rule, 1);
        }

        return steps;
    }

    /// <summary>How many ways there are to pick up to <paramref name="picks"/> of <paramref name="from"/> in order.</summary>
    internal static double Choices(int from, int picks)
    {
        var term = 1d;
        var sum = 1d;

        for (var j = 1; j <= Math.Min(picks, from) && !double.IsInfinity(sum); j++)
        {
            term = term * (from - j + 1) / j;
            sum += term;
        }

        return sum;
    }

    /// <summary>The largest figures of the real document. Nothing can make matching wider than these.</summary>
    internal readonly record struct TreeShape(int Depth, int Siblings, int Elements)
    {
        public static TreeShape Of(IElement root, ConversionBudget budget)
        {
            var (depth, siblings, elements) = (0, 0, 0);
            var pending = new Stack<(IElement Element, int Depth)>();
            pending.Push((root, 1));

            while (pending.TryPop(out var item))
            {
                budget.Check();
                elements++;
                depth = Math.Max(depth, item.Depth);
                siblings = Math.Max(siblings, item.Element.Children.Length);

                foreach (var child in item.Element.Children)
                    pending.Push((child, item.Depth + 1));
            }

            return new TreeShape(depth, siblings, elements);
        }
    }

    private sealed class Estimate(TreeShape tree, ICssSelectorParser parser, ConversionBudget budget) : ISelectorVisitor
    {
        private double _cost;

        public double OfRule(ICssStyleRule rule, double above)
        {
            var own = Of(rule.Selector);
            // A selector the engine could not read matches nothing, and 0 times infinity is not 0.
            var total = own == 0 ? 0 : own * above;

            foreach (var nested in rule.Rules.OfType<ICssStyleRule>())
                total += OfRule(nested, above * tree.Depth * Math.Max(own, 1));

            return total;
        }

        private double Of(ISelector? selector)
        {
            if (selector is null)
                return 0;

            var estimate = new Estimate(tree, parser, budget);
            selector.Accept(estimate);
            return estimate._cost;
        }

        public void Attribute(string name, string op, string? value) => _cost = 1;

        public void Type(string name) => _cost = 1;

        public void Id(string value) => _cost = 1;

        public void Class(string name) => _cost = 1;

        public void PseudoElement(string name) => _cost = 1;

        public void Child(string name, int step, int offset, ISelector selector) =>
            _cost = tree.Siblings * Math.Max(Of(selector), 1);

        public void List(IEnumerable<ISelector> selectors) => _cost = selectors.Sum(Of);

        public void Many(IEnumerable<ISelector> selectors) => _cost = selectors.Sum(Of);

        public void Combinator(IEnumerable<ISelector> selectors, IEnumerable<string> symbols)
        {
            var (ancestors, run, paths) = (0, 0, 1d);

            foreach (var symbol in symbols)
            {
                if (symbol == CombinatorSymbols.Sibling)
                {
                    run++;
                    continue;
                }

                paths *= Choices(tree.Siblings, run);
                run = 0;

                if (symbol == CombinatorSymbols.Descendant)
                    ancestors++;
                else if (symbol == CombinatorSymbols.Column)
                    paths *= tree.Elements;
                else if (symbol != CombinatorSymbols.Child && symbol != CombinatorSymbols.Adjacent
                    && symbol != CombinatorSymbols.Deep && symbol != CombinatorSymbols.Pipe)
                    paths = double.PositiveInfinity;
            }

            paths *= Choices(tree.Siblings, run) * Choices(tree.Depth, ancestors);
            _cost = paths * selectors.Select(Of).DefaultIfEmpty(1).Max();
        }

        public void PseudoClass(string name)
        {
            var bracket = name.IndexOf('(');
            if (bracket < 0)
            {
                _cost = 1;
                return;
            }

            budget.Check();
            var function = name[..bracket].ToLowerInvariant();
            var argument = name[(bracket + 1)..^1];

            _cost = function switch
            {
                "lang" or "dir" => 1,
                "has" => tree.Elements * Argument(argument),
                "not" or "is" or "where" or "matches" or "host" or "host-context" => Argument(argument),
                _ => double.PositiveInfinity
            };
        }

        // An empty argument is what a forgiving list leaves of a selector it could not read.
        private double Argument(string text) => text.Length == 0
            ? 1
            : parser.ParseSelector(text) is { } selector ? Math.Max(Of(selector), 1) : double.PositiveInfinity;
    }
}
