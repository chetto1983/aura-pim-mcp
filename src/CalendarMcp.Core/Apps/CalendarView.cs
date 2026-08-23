using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Apps;

/// <summary>
/// The MCP Apps view for the curated <c>calendar</c> tool: a three-pane client the host
/// renders in a sandboxed iframe instead of showing the tool's JSON.
/// </summary>
/// <remarks>
/// <para>
/// MCP Apps binds one <c>ui://</c> resource to one tool through the tool's <c>_meta.ui</c>,
/// and this server publishes a single action-multiplexed tool, so there is exactly one view
/// and it dispatches on the payload family it receives.
/// </para>
/// <para>
/// The document is assembled here rather than shipped pre-built. A <c>ui://</c> resource is
/// ONE self-contained HTML string -- the host has no second request to fetch a stylesheet or
/// a script with, and the CSP this resource declares would refuse it anyway -- so the theme
/// and the transport bridge are kept as separate authorable files and inlined at read time.
/// The three files are embedded with explicit <c>LogicalName</c>s (see the csproj) because
/// the default manifest name is derived from the root namespace and folder path, which makes
/// renaming a directory silently break resource lookup at runtime instead of at build.
/// </para>
/// <para>
/// The bridge is shared verbatim with chetto1983/whatsapp-mcp's <c>ui/_bridge.js</c>: it
/// speaks the ext-apps postMessage transport and knows nothing about calendars, so the two
/// forks carry one implementation rather than two that drift.
/// </para>
/// </remarks>
public static class CalendarView
{
    /// <summary>The <c>ui://</c> URI naming this view, referenced by the tool's <c>_meta.ui</c>.</summary>
    public const string ResourceUri = "ui://calendar/view.html";

    private const string HtmlResource = "calendar-app/view.html";
    private const string ThemeResource = "calendar-app/theme.css";
    private const string BridgeResource = "calendar-app/bridge.js";

    private const string ThemePlaceholder = "/*{{THEME}}*/";
    private const string BridgePlaceholder = "/*{{BRIDGE}}*/";

    private static readonly Lazy<string> Document = new(Compose, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The assembled single-file HTML document served for <see cref="ResourceUri"/>.</summary>
    public static string Html => Document.Value;

    private static string Compose()
    {
        var html = ReadEmbedded(HtmlResource);
        foreach (var (placeholder, resource) in
                 new[] { (ThemePlaceholder, ThemeResource), (BridgePlaceholder, BridgeResource) })
        {
            // A missing placeholder means the view was edited apart from this composer, which
            // would ship a document with no styling or -- worse -- no transport, and fail as a
            // blank frame with nothing in the logs. Fail at first read instead.
            if (!html.Contains(placeholder, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The MCP Apps view is missing its '{placeholder}' placeholder; '{resource}' cannot be inlined.");

            html = html.Replace(placeholder, ReadEmbedded(resource), StringComparison.Ordinal);
        }

        return html;
    }

    private static string ReadEmbedded(string logicalName)
    {
        var assembly = typeof(CalendarView).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' is missing. Available: " +
                string.Join(", ", assembly.GetManifestResourceNames()));

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // The MCP Apps extension is SEP-1865, still experimental, and the C# SDK marks its whole
    // surface MCPEXP003. Suppressed at the two members that touch it rather than project-wide,
    // so the day the types move the compiler points at exactly this code.
#pragma warning disable MCPEXP003

    /// <summary>
    /// The <c>_meta.ui</c> the host reads to build the iframe's Content-Security-Policy.
    /// </summary>
    /// <remarks>
    /// Every domain list is empty ON PURPOSE, and that is a claim the view has to keep: it
    /// must never fetch. The first payload arrives over postMessage, and everything further is
    /// a <c>tools/call</c> routed back through the host -- which is also what lets the host
    /// apply its own approval policy to a call the view makes on the user's behalf. An empty
    /// allowlist is therefore the accurate description of this app, not a precaution.
    /// </remarks>
    private static McpUiResourceMeta BuildMeta() => new()
    {
        Csp = new McpUiResourceCsp
        {
            ConnectDomains = [],
            ResourceDomains = [],
            FrameDomains = [],
            BaseUris = [],
        },
        Permissions = new McpUiResourcePermissions { Allow = [] },
        // The view draws its own frame (rounded rule around the three panes), so a second
        // border from the host would double it.
        PrefersBorder = false,
    };

    /// <summary>
    /// Registers the view resource and points the curated <c>calendar</c> tool at it.
    /// </summary>
    /// <remarks>
    /// Registration is imperative, matching <c>WithCalendarActionTool()</c>: the tool is built
    /// by hand in a factory so its action enum can be patched into the schema, and the same
    /// place is where its <c>_meta.ui</c> is set. Attribute-driven registration would split one
    /// decision across two mechanisms.
    /// </remarks>
    public static IMcpServerBuilder WithCalendarView(this IMcpServerBuilder builder)
    {
        builder.Services.AddSingleton<McpServerResource>(_ =>
        {
            var resource = McpServerResource.Create(
                () => new TextResourceContents
                {
                    Uri = ResourceUri,
                    MimeType = McpApps.HtmlMimeType,
                    Text = Html,
                },
                new McpServerResourceCreateOptions
                {
                    UriTemplate = ResourceUri,
                    Name = "calendar-view",
                    Title = "Calendar",
                    Description = "Three-pane view of the accounts, calendars, events, messages and contacts returned by the calendar tool.",
                    MimeType = McpApps.HtmlMimeType,
                });

            // NOT McpApps.SetResourceUi: that helper writes to ProtocolResourceTemplate.Meta, and
            // this resource has no URI parameters, so the server publishes it through
            // ProtocolResource instead -- measured 2026-08-23, resources/templates/list came back
            // empty while resources/list carried the entry with no _meta at all, which would have
            // shipped the view with its CSP declaration silently missing.
            var published = resource.ProtocolResource
                ?? throw new InvalidOperationException(
                    "The calendar view resolved as a templated resource; its _meta.ui would not be published.");

            (published.Meta ??= [])["ui"] =
                JsonSerializer.SerializeToNode(BuildMeta(), typeof(McpUiResourceMeta), McpApps.SerializerOptions);

            return resource;
        });

        return builder;
    }

#pragma warning restore MCPEXP003
}
