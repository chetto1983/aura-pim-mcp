using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;

namespace CalendarMcp.Core.Utilities;

/// <summary>
/// The one door CSS comes through: style sheets, inline styles and substituted variables all ask the
/// browsing context for an <see cref="ICssParser"/>, and text here is already decoded, so an entity
/// cannot hide a bracket. Only what passes <see cref="CssShape"/> reaches the engine.
/// </summary>
/// <remarks>
/// A failure inside a style element does not propagate: the element's task is completed, which
/// <c>Document.DelayLoad</c> ignores, and the sheet is left null. Quietly losing a sheet shows the
/// model everything its rules hid, so a refusal is also remembered for <see cref="ThrowIfRefused"/>.
/// </remarks>
internal sealed class GuardedCssParser(ICssParser engine) : EventTarget, ICssParser
{
    private const string TooDeep = "The styles of this mail are nested too deeply to read, so its body is left out. The other fields are unaffected.";

    private bool _refused;

    public event DomEventHandler Parsing
    {
        add => engine.Parsing += value;
        remove => engine.Parsing -= value;
    }

    public event DomEventHandler Parsed
    {
        add => engine.Parsed += value;
        remove => engine.Parsed -= value;
    }

    public event DomEventHandler Error
    {
        add => engine.Error += value;
        remove => engine.Error -= value;
    }

    public void ThrowIfRefused()
    {
        if (_refused)
            throw new UnconvertibleMailException(TooDeep);
    }

    public ICssStyleSheet ParseStyleSheet(string content) => engine.ParseStyleSheet(Checked(content));

    public ICssRule? ParseRule(ICssStyleSheet owner, string content) => engine.ParseRule(owner, Checked(content));

    public ICssKeyframeRule? ParseKeyframeRule(ICssStyleSheet owner, string content) =>
        engine.ParseKeyframeRule(owner, Checked(content));

    public ICssStyleDeclaration? ParseDeclaration(string content) => engine.ParseDeclaration(Checked(content));

    public Task<ICssStyleSheet> ParseStyleSheetAsync(string content, CancellationToken cancelToken) =>
        engine.ParseStyleSheetAsync(Checked(content), cancelToken);

    // Nothing here reaches these, and an unread stream cannot be checked.
    public ICssStyleSheet ParseStyleSheet(Stream content) => throw Unchecked();

    public Task<ICssStyleSheet> ParseStyleSheetAsync(Stream content, CancellationToken cancelToken) => throw Unchecked();

    /// <summary>
    /// How a style element arrives. Its <c>media</c> attribute goes to a parser of its own that
    /// recurses too and never asks for this one, so the attribute is checked here with the text.
    /// </summary>
    public async Task<ICssStyleSheet> ParseStyleSheetAsync(ICssStyleSheet sheet, CancellationToken cancelToken)
    {
        try
        {
            await sheet.Source.PrefetchAllAsync(cancelToken).ConfigureAwait(false);
            Checked(sheet.Source.Text);
            Checked(sheet.OwnerNode?.GetAttribute("media") ?? string.Empty);

            return await engine.ParseStyleSheetAsync(sheet, cancelToken).ConfigureAwait(false);
        }
        catch
        {
            _refused = true;
            throw;
        }
    }

    private string Checked(string css)
    {
        if (CssShape.IsSafe(css))
            return css;

        _refused = true;
        throw new UnconvertibleMailException(TooDeep);
    }

    private NotSupportedException Unchecked()
    {
        _refused = true;
        return new NotSupportedException("A stylesheet stream cannot be checked before it is parsed.");
    }
}
