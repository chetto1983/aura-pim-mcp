using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using ReverseMarkdown.Preprocessing;

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
    private const double DefaultFontPixels = 16;
    private const int SameColourDistance = 12;
    private static readonly Rgb PageBackground = new(255, 255, 255);

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
    private sealed record Look(bool HidesItsContent, bool IsUnseen, double FontPixels, string? Visibility, string? Color, string? Background)
    {
        public static readonly Look Page = new(false, false, DefaultFontPixels, null, null, null);
    }

    /// <summary>Everything is judged before anything goes: a removed <c>&lt;style&gt;</c> rule cannot judge what follows it.</summary>
    /// <exception cref="UnconvertibleMailException">
    /// The tree reaches the parser's depth limit. Past it, the parser attaches an element's children
    /// beside it instead of inside it (<c>HtmlParserOptions.MaximumDepth</c>), so the tree can no longer
    /// be trusted to say what a hidden ancestor contains: <c>display:none</c> on the last level
    /// would lose its children to the visible level above. Nothing short of refusing covers that.
    /// </exception>
    public static void Remove(IElement root, ConversionBudget budget)
    {
        var styles = root.Owner!.DefaultView!.GetStyleCollection(Reader);
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
        var style = Cascaded(element, styles);
        string? Value(string property) => Nullable(style.GetPropertyValue(property));

        var font = FontPixels(Value("font-size"), above.FontPixels);
        var visibility = Inherited(Value("visibility"), above.Visibility);
        var color = Inherited(Value("color"), above.Color);
        var background = Background(element, Value) ?? above.Background;

        var hides = Value("display") == "none"
            || IsZero(Value("opacity"))
            || InlineStyle.Has(element, "mso-hide", "all")
            || IsClippedBox(Value)
            || IsOffScreen(Value, font);

        var unseen = hides || font <= TinyFontPixels || visibility is "hidden" or "collapse" || BlendsIn(color, background);

        return new Look(hides, unseen, font, visibility, color, background);
    }

    // Only the engine can work out var() and calc(), at the price of the element's whole ancestor
    // chain, so it is asked only about an element that needs it.
    private static ICssStyleDeclaration Cascaded(IElement element, IStyleCollection styles)
    {
        var declared = styles.ComputeExplicitStyle(element);

        foreach (var property in declared)
        {
            if (ComputedFunction().IsMatch(property.Value))
                return styles.ComputeDeclarations(element);
        }

        return declared;
    }

    private static string? Inherited(string? own, string? above) =>
        own is null or "inherit" or "unset" or "revert" or "revert-layer" or "currentcolor" ? above : own;

    private static double FontPixels(string? value, double above)
    {
        if (ReadNumber(value) is { Unit: "%" } percent)
            return percent.Value * above / 100;

        return Pixels(value, above) ?? above;
    }

    private static bool IsClippedBox(Func<string, string?> value)
    {
        var clipped = value("overflow") is "hidden" or "clip"
            || value("overflow-x") is "hidden" or "clip"
            || value("overflow-y") is "hidden" or "clip";

        return clipped && (IsZero(value("height")) || IsZero(value("max-height"))
            || IsZero(value("width")) || IsZero(value("max-width")));
    }

    private static bool IsOffScreen(Func<string, string?> value, double font)
    {
        if (Pixels(value("text-indent"), font) <= OffScreenPixels)
            return true;

        if (ZeroClip().IsMatch(value("clip") ?? string.Empty))
            return true;

        return value("position") is "absolute" or "fixed"
            && (Pixels(value("left"), font) <= OffScreenPixels || Pixels(value("top"), font) <= OffScreenPixels);
    }

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

    private static bool IsZero(string? value) => ReadNumber(value) is { Value: 0 };

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

    [GeneratedRegex(@"^\s*(-?\d{0,32}\.?\d{1,32})\s*([a-zA-Z%]{0,8})\s*$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^\s*#([0-9a-fA-F]{2})([0-9a-fA-F]{2})([0-9a-fA-F]{2})\s*$")]
    private static partial Regex HexColour();

    // Nine digits fit an int, so the parse above cannot overflow, and a longer number is no colour.
    [GeneratedRegex(@"rgba?\(\s*(\d{1,9})\s*,\s*(\d{1,9})\s*,\s*(\d{1,9})\s*(?:,\s*([\d.]{1,32})\s*)?\)")]
    private static partial Regex RgbColour();

    [GeneratedRegex(@"rect\(\s*0[a-z]*[\s,]+0[a-z]*[\s,]+0[a-z]*[\s,]+0[a-z]*\s*\)")]
    private static partial Regex ZeroClip();
}
