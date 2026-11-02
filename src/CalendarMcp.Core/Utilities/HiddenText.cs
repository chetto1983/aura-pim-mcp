using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;

namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Removes text the person reading a mail cannot see, because a model would still read it. One walk
/// from the top hands each element what its ancestors said, because email layouts set
/// <c>font-size:0</c> on a wrapper and reset it on the children: removing the wrapper would delete a
/// visible column.
/// </summary>
/// <remarks>
/// <see cref="StyleCollectionExtensions.ComputeExplicitStyle"/> and not <c>ComputeCurrentStyle</c>: the
/// second builds a new rule index and computes the whole ancestor chain for every call, which measured
/// about 300 ms on a 62 KB newsletter and 5 s on 500 levels. The first costs a millisecond or two on
/// that newsletter, and leaves inheritance to this walk, which needs it anyway.
/// </remarks>
internal static partial class HiddenText
{
    private const double OffScreenPixels = -100;
    private const double TinyFontPixels = 2;
    private const double TinyBoxPixels = 2;
    private const double FaintOpacity = 0.1;
    private const double DefaultFontPixels = 16;
    private const int SameColourDistance = 12;

    /// <summary>
    /// The longest a value may get once its var() are replaced. The engine pastes variables in as
    /// text, up to 1 MiB, and only then parses what came out, and calc() recurses on every bracket and
    /// every term of it. <see cref="CssShape"/> saw the pieces, and nothing can interrupt the parse, so
    /// 560 bytes of variables that each paste the next one twice overflowed an 8 MiB stack. Text of this
    /// length needs 591 KB at worst, all of it brackets, in a fresh process on the coldest code.
    /// </summary>
    internal const int MaxPasted = 2048;

    /// <summary>What the engine can add around a pasted value to keep it from merging with its neighbours.</summary>
    private const int PasteSeparators = 8;

    private static readonly Rgb PageBackground = new(255, 255, 255);
    private static readonly string[] Margins = ["margin-left", "margin-right", "margin-top", "margin-bottom"];
    private static readonly string[] Offsets = ["left", "right", "top", "bottom"];

    /// <summary>
    /// What a person reads a mail on. The engine's default device has no width at all, which
    /// answers <c>max-width</c> rules as true and cannot resolve a percentage.
    /// </summary>
    private static readonly DefaultRenderDevice Reader = new()
    {
        ViewPortWidth = 1024,
        ViewPortHeight = 768,
        DeviceWidth = 1024,
        DeviceHeight = 768,
        IsScripting = false
    };

    private readonly record struct Rgb(int Red, int Green, int Blue);

    /// <summary>What an element hands down, and whether its own text can be seen.</summary>
    private sealed record Look(bool HidesItsContent, bool IsUnseen, double FontPixels, string? Visibility, string? Color, string? Background, int LongestVariable)
    {
        public static readonly Look Page = new(false, false, DefaultFontPixels, null, null, null, 0);
    }

    /// <summary>Everything is judged before anything goes: a removed <c>&lt;style&gt;</c> rule cannot judge what follows it.</summary>
    /// <exception cref="UnconvertibleMailException">
    /// The tree reaches the parser's depth limit. Past it, the parser attaches an element's children
    /// beside it instead of inside it (<c>HtmlParserOptions.MaximumDepth</c>), so the tree can no longer
    /// be trusted to say what a hidden ancestor contains: <c>display:none</c> on the last level
    /// would lose its children to the visible level above. Nothing short of refusing covers that.
    /// A value that would paste more than <see cref="MaxPasted"/> is refused the same way, and so is a
    /// sheet <see cref="SelectorCost"/> finds could not be matched in time.
    /// </exception>
    public static void Remove(IElement root, ConversionBudget budget)
    {
        var styles = root.Owner!.DefaultView!.GetStyleCollection(Reader);
        SelectorCost.Check(root, styles, budget);

        var unseen = new List<IChildNode>();
        var pending = new Stack<(INode Node, Look Above, int Depth)>();
        pending.Push((root, Look.Page, 1));

        while (pending.TryPop(out var item))
        {
            budget.Check();
            var (node, above, depth) = item;

            if (node is IText text)
            {
                if (above.IsUnseen && !string.IsNullOrWhiteSpace(text.Data))
                    unseen.Add(text);
            }
            else if (node is IElement element)
            {
                if (depth >= MailBody.MaxDepth)
                    throw new UnconvertibleMailException(MailBody.TooDeep);

                var look = above.HidesItsContent ? above : Read(element, styles, above);
                if (look.IsUnseen && element.LocalName == "img" && element.HasAttribute("alt"))
                    unseen.Add(element);

                foreach (var child in element.ChildNodes)
                    pending.Push((child, look, depth + 1));
            }
        }

        foreach (var node in unseen)
        {
            budget.Check();
            node.Remove();
        }
    }

    private static Look Read(IElement element, IStyleCollection styles, Look above)
    {
        var declared = styles.ComputeExplicitStyle(element);
        var longestVariable = LongestVariable(declared, above.LongestVariable);
        var style = Cascaded(element, declared, styles, longestVariable);
        string? Value(string property) => Nullable(style.GetPropertyValue(property));

        var font = FontPixels(Value("font-size"), above.FontPixels);
        var visibility = Inherited(Value("visibility"), above.Visibility);
        var color = Inherited(Value("color"), above.Color);
        var background = Background(element, Value) ?? above.Background;

        var hides = Value("display") == "none"
            || IsFaint(Value("opacity"))
            || Value("mso-hide") == "all"
            || IsClippedBox(Value, font)
            || IsClippedAway(Value("clip-path"), font)
            || IsOffScreen(Value, font);

        var unseen = hides || font <= TinyFontPixels || visibility is "hidden" or "collapse" || BlendsIn(color, background)
            || IsPixel(element, Value, font);

        return new Look(hides, unseen, font, visibility, color, background, longestVariable);
    }

    // Only the engine can work out var() and calc(), at the price of the element's whole ancestor
    // chain, so it is asked only about an element that needs it. It works out every value of every
    // one of them, so every value is checked, and this walk has reached the ancestors first.
    private static ICssStyleDeclaration Cascaded(IElement element, ICssStyleDeclaration declared, IStyleCollection styles, int longestVariable)
    {
        var needsTheEngine = false;

        foreach (var property in declared)
        {
            if (Pasted(property.Value, longestVariable) > MaxPasted)
                throw new UnconvertibleMailException(MailBody.TooDeep);

            needsTheEngine |= ComputedFunction().IsMatch(property.Value);
        }

        return needsTheEngine ? styles.ComputeDeclarations(element) : declared;
    }

    /// <summary>
    /// The longest text a var() can paste at this element. What comes down from the ancestors is
    /// already worked out, but variables declared here can paste each other in any order, so each
    /// pass settles one more link, and a chain has no more links than there are variables pasting.
    /// </summary>
    private static int LongestVariable(ICssStyleDeclaration declared, int above)
    {
        var variables = declared
            .Where(property => property.Name.StartsWith("--", StringComparison.Ordinal))
            .Select(property => (Length: property.Value.Length + PasteSeparators, Pastes: Substitution().Count(property.Value)))
            .ToList();

        var longest = variables.Select(variable => variable.Length).Append(above).Max();
        var pasting = variables.Where(variable => variable.Pastes > 0).ToList();

        for (var link = 0; link < pasting.Count && longest <= MaxPasted; link++)
            longest = pasting.Select(variable => variable.Length + (variable.Pastes * longest)).Append(longest).Max();

        return longest;
    }

    /// <summary>How long a value gets if each of its var() pastes the longest variable there is, or 0 if none can.</summary>
    private static int Pasted(string value, int longestVariable)
    {
        var pasted = Substitution().Count(value) * longestVariable;
        return pasted == 0 ? 0 : value.Length + pasted;
    }

    private static string? Inherited(string? own, string? above) =>
        own is null or "inherit" or "unset" or "revert" or "revert-layer" or "currentcolor" ? above : own;

    private static double FontPixels(string? value, double above)
    {
        if (ReadNumber(value) is { Unit: "%" } percent)
            return percent.Value * above / 100;

        return Pixels(value, above) ?? above;
    }

    private static bool IsFaint(string? opacity) =>
        ReadNumber(opacity) is { } number && (number.Unit == "%" ? number.Value / 100 : number.Value) <= FaintOpacity;

    private static bool IsTiny(string? length, double font) => Pixels(length, font) <= TinyBoxPixels;

    private static bool IsClippedBox(Func<string, string?> value, double font)
    {
        var clipped = value("overflow") is "hidden" or "clip"
            || value("overflow-x") is "hidden" or "clip"
            || value("overflow-y") is "hidden" or "clip";

        return clipped && (IsTiny(value("height"), font) || IsTiny(value("max-height"), font)
            || IsTiny(value("width"), font) || IsTiny(value("max-width"), font));
    }

    /// <summary>An <c>inset()</c> whose opposite sides meet, in its one to four sides as CSS shorthand gives them.</summary>
    private static bool IsClippedAway(string? clipPath, double font)
    {
        var sides = InsetClip().Match(clipPath ?? string.Empty).Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (sides.Length == 0)
            return false;

        var (top, right, bottom, left) = sides.Length switch
        {
            1 => (0, 0, 0, 0),
            2 => (0, 1, 0, 1),
            3 => (0, 1, 2, 1),
            _ => (0, 1, 2, 3)
        };

        double Percent(int side) => ReadNumber(sides[side]) is { Unit: "%" } share
            ? share.Value
            : Pixels(sides[side], font) >= Reader.ViewPortWidth ? 100 : 0;

        return Percent(top) + Percent(bottom) >= 100 || Percent(left) + Percent(right) >= 100;
    }

    /// <summary>
    /// A margin only moves a box from where it would be, so it has to outrun the whole screen to prove
    /// anything, where an offset on something positioned has less to go: layouts overlap with both.
    /// </summary>
    private static bool IsOffScreen(Func<string, string?> value, double font)
    {
        if (Pixels(value("text-indent"), font) <= OffScreenPixels)
            return true;

        if (ZeroClip().IsMatch(value("clip") ?? string.Empty))
            return true;

        if (Margins.Any(margin => Pixels(value(margin), font) <= -Reader.ViewPortWidth))
            return true;

        return value("position") is "absolute" or "fixed" or "relative"
            && (Offsets.Any(offset => Pixels(value(offset), font) <= OffScreenPixels)
                || Pixels(value("left"), font) >= Reader.ViewPortWidth);
    }

    private static bool IsPixel(IElement element, Func<string, string?> value, double font) =>
        element.LocalName == "img"
        && (IsTiny(value("width") ?? element.GetAttribute("width"), font)
            || IsTiny(value("height") ?? element.GetAttribute("height"), font));

    private static string? Background(IElement element, Func<string, string?> value)
    {
        if (element.HasAttribute("background") || value("background-image")?.Contains('(') == true)
            return "image";

        var colour = value("background-color") ?? element.GetAttribute("bgcolor");
        return Rgba(colour) is { Alpha: > 0 } ? colour : null;
    }

    private static bool BlendsIn(string? color, string? background)
    {
        if (Rgba(color) is not { } text)
            return false;
        if (text.Alpha == 0)
            return true;
        if (background == "image")
            return false;

        var behind = Rgba(background)?.Colour ?? PageBackground;
        var distance = Math.Abs(text.Colour.Red - behind.Red)
            + Math.Abs(text.Colour.Green - behind.Green)
            + Math.Abs(text.Colour.Blue - behind.Blue);
        return distance <= SameColourDistance;
    }

    private static (Rgb Colour, double Alpha)? Rgba(string? value)
    {
        if (value is null)
            return null;

        var hex = HexColour().Match(value);
        if (hex.Success)
            return (new Rgb(FromHex(hex, 1), FromHex(hex, 2), FromHex(hex, 3)), 1);

        var rgb = RgbColour().Match(value);
        if (!rgb.Success)
            return null;

        var alpha = 1d;
        if (rgb.Groups[4].Success && !double.TryParse(rgb.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out alpha))
            return null;

        return (new Rgb(int.Parse(rgb.Groups[1].Value), int.Parse(rgb.Groups[2].Value), int.Parse(rgb.Groups[3].Value)), alpha);

        static int FromHex(Match match, int group) => Convert.ToInt32(match.Groups[group].Value, 16);
    }

    /// <summary>A declared length in pixels, or null when it is not one. The engine resolves none of these in a declared value.</summary>
    private static double? Pixels(string? value, double fontPixels)
    {
        if (ReadNumber(value) is not { } length)
            return null;

        return length.Unit.ToLowerInvariant() switch
        {
            "" or "px" => length.Value,
            "pt" => length.Value * 96 / 72,
            "pc" => length.Value * 16,
            "in" => length.Value * 96,
            "cm" => length.Value * 96 / 2.54,
            "mm" => length.Value * 96 / 25.4,
            "em" => length.Value * fontPixels,
            "ex" or "ch" => length.Value * fontPixels / 2,
            "rem" => length.Value * DefaultFontPixels,
            "%" or "vw" => length.Value * Reader.ViewPortWidth / 100,
            "vh" => length.Value * Reader.ViewPortHeight / 100,
            _ => null
        };
    }

    // An attribute reaches this unparsed and unchecked, so its text is anyone's. The parse must not throw.
    private static (double Value, string Unit)? ReadNumber(string? value)
    {
        var match = Number().Match(value ?? string.Empty);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;

        return (number, match.Groups[2].Value);
    }

    private static string? Nullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"\b(?:var|calc|min|max|clamp)\(", RegexOptions.IgnoreCase)]
    private static partial Regex ComputedFunction();

    // An escape can spell var, which the engine reads as the function, so every one counts as a paste.
    [GeneratedRegex(@"var\(|\\", RegexOptions.IgnoreCase)]
    private static partial Regex Substitution();

    // Any length: the engine writes 1e-40 out in full, and a number this stops reading means visible text.
    // The atomic groups keep it linear, and a body's cap is what bounds the text.
    [GeneratedRegex(@"^\s*([-+]?(?>\d*)(?:\.(?>\d+))?(?:[eE][-+]?(?>\d+))?)\s*([a-zA-Z%]{0,8})\s*$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^\s*#([0-9a-fA-F]{2})([0-9a-fA-F]{2})([0-9a-fA-F]{2})\s*$")]
    private static partial Regex HexColour();

    // Nine digits fit an int, so the parse above cannot overflow, and a longer number is no colour.
    [GeneratedRegex(@"rgba?\(\s*(\d{1,9})\s*,\s*(\d{1,9})\s*,\s*(\d{1,9})\s*(?:,\s*([\d.]{1,32})\s*)?\)")]
    private static partial Regex RgbColour();

    [GeneratedRegex(@"rect\(\s*0[a-z]*[\s,]+0[a-z]*[\s,]+0[a-z]*[\s,]+0[a-z]*\s*\)")]
    private static partial Regex ZeroClip();

    [GeneratedRegex(@"^\s*inset\(\s*([^()]*?)\s*(?:round\b[^()]*)?\)", RegexOptions.IgnoreCase)]
    private static partial Regex InsetClip();
}
