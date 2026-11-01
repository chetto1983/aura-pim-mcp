using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using ReverseMarkdown.Preprocessing;

namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Removes text the person reading a mail cannot see, because a model would still read it. Text is
/// judged node by node, walking up its ancestors, because email layouts set <c>font-size:0</c> on
/// a wrapper and reset it on the children: removing the wrapper would delete a visible column.
/// AngleSharp.Css resolves the cascade, including <c>&lt;style&gt;</c> rules, but not inheritance,
/// so the walk supplies that.
/// </summary>
internal static partial class HiddenText
{
    private const double OffScreenPixels = -100;
    private const int SameColourDistance = 12;
    private static readonly Rgb PageBackground = new(255, 255, 255);

    private readonly record struct Rgb(int Red, int Green, int Blue);

    /// <summary>What one element says, for itself and for whatever it inherits downward.</summary>
    private sealed record Look(bool HidesItsContent, string? FontSize, string? Visibility, string? Color, string? Background);

    public static void Remove(IElement root)
    {
        var looks = new Dictionary<IElement, Look>();

        // Everything is judged before anything goes. A <style> computes to display:none, so
        // removing its text along the way would empty the cascade for every later node.
        var hiddenTexts = root.Descendants<IText>()
            .Where(text => !string.IsNullOrWhiteSpace(text.Data) && IsHidden(text.ParentElement, looks))
            .ToList();
        var hiddenImages = root.QuerySelectorAll("img[alt]")
            .Where(image => IsHidden(image, looks))
            .ToList();

        foreach (var text in hiddenTexts)
            text.Remove();
        foreach (var image in hiddenImages)
            image.Remove();
    }

    private static bool IsHidden(IElement? start, Dictionary<IElement, Look> looks)
    {
        string? fontSize = null, visibility = null, color = null, background = null;

        for (var element = start; element is not null; element = element.ParentElement)
        {
            if (!looks.TryGetValue(element, out var look))
                looks[element] = look = Read(element);

            if (look.HidesItsContent)
                return true;

            fontSize ??= look.FontSize;
            visibility ??= look.Visibility;
            color ??= look.Color;
            background ??= look.Background;
        }

        return IsTiny(fontSize)
            || visibility is "hidden" or "collapse"
            || BlendsIn(color, background);
    }

    private static Look Read(IElement element)
    {
        var style = element.ComputeCurrentStyle();
        string? Value(string property) => Nullable(style.GetPropertyValue(property));

        var hides = Value("display") == "none"
            || IsZero(Value("opacity"))
            || InlineStyle.Has(element, "mso-hide", "all")
            || IsClippedBox(Value)
            || IsOffScreen(Value);

        return new Look(hides, Value("font-size"), Value("visibility"), Value("color"), Background(element, Value));
    }

    private static bool IsClippedBox(Func<string, string?> value)
    {
        var clipped = value("overflow") is "hidden" or "clip"
            || value("overflow-x") is "hidden" or "clip"
            || value("overflow-y") is "hidden" or "clip";

        return clipped && (IsZero(value("height")) || IsZero(value("max-height"))
            || IsZero(value("width")) || IsZero(value("max-width")));
    }

    private static bool IsOffScreen(Func<string, string?> value)
    {
        if (Pixels(value("text-indent")) <= OffScreenPixels)
            return true;

        if (ZeroClip().IsMatch(value("clip") ?? string.Empty))
            return true;

        return value("position") is "absolute" or "fixed"
            && (Pixels(value("left")) <= OffScreenPixels || Pixels(value("top")) <= OffScreenPixels);
    }

    private static string? Background(IElement element, Func<string, string?> value)
    {
        if (element.HasAttribute("background") || value("background-image") is not (null or "none"))
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

        var alpha = rgb.Groups[4].Success ? double.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
        return (new Rgb(int.Parse(rgb.Groups[1].Value), int.Parse(rgb.Groups[2].Value), int.Parse(rgb.Groups[3].Value)), alpha);

        static int FromHex(Match match, int group) => Convert.ToInt32(match.Groups[group].Value, 16);
    }

    private static bool IsTiny(string? fontSize)
    {
        var match = Number().Match(fontSize ?? string.Empty);
        if (!match.Success)
            return false;

        var size = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "em" or "rem" => size <= 0.1,
            "%" => size <= 10,
            _ => size <= 1
        };
    }

    private static bool IsZero(string? value)
    {
        var match = Number().Match(value ?? string.Empty);
        return match.Success && double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == 0;
    }

    private static double Pixels(string? value)
    {
        var match = Number().Match(value ?? string.Empty);
        return match.Success && match.Groups[2].Value is "" or "px"
            ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    private static string? Nullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^\s*(-?\d*\.?\d+)\s*([a-zA-Z%]*)\s*$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^\s*#([0-9a-fA-F]{2})([0-9a-fA-F]{2})([0-9a-fA-F]{2})\s*$")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)")]
    private static partial Regex RgbColour();

    [GeneratedRegex(@"rect\(\s*0[a-z]*[\s,]+0[a-z]*[\s,]+0[a-z]*[\s,]+0[a-z]*\s*\)")]
    private static partial Regex ZeroClip();
}
