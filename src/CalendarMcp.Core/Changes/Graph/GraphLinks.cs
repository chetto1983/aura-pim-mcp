using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;

namespace CalendarMcp.Core.Changes.Graph;

/// <summary>The rules for a delta link, which Graph writes and a cursor carries back unsigned.</summary>
internal static class GraphLinks
{
    private const string Host = "graph.microsoft.com";

    /// <summary>
    /// Asks for an id that lasts. A mail's default id changes when it moves, Deleted Items included,
    /// so one kept for later reads as gone while the mail is still there. The header holds only for
    /// the request it is on, and delta links take both formats, so nothing resyncs
    /// (https://learn.microsoft.com/en-us/graph/outlook-immutable-id). Events keep their default id
    /// on purpose: a deleted event is told by that id no longer resolving, and Aura finds an event's
    /// earlier start by it.
    /// </summary>
    public static void AskForLastingIds(RequestHeaders headers) => headers.Add("Prefer", "IdType=\"ImmutableId\"");

    /// <summary>
    /// The link, if it can be followed. <see cref="Providers.BearerTokenAuthenticationProvider"/> puts
    /// the account's token on every request whatever the host, so a forged cursor must fail here: a
    /// link anywhere else would hand that token to whoever wrote it.
    /// </summary>
    public static string Checked(string link)
    {
        var isGraph = Uri.TryCreate(link, UriKind.Absolute, out var uri)
                      && uri.Scheme == Uri.UriSchemeHttps
                      && uri.UserInfo.Length == 0
                      && uri.IsDefaultPort
                      && uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);

        return isGraph ? link : throw ChangesException.InvalidCursor();
    }

    /// <summary>
    /// Whether the position is gone. https://learn.microsoft.com/en-us/graph/delta-query-overview says
    /// 410 for a reset, and for Outlook a "40X-series error with error codes such as syncStateNotFound".
    /// </summary>
    public static bool IsExpired(ApiException failure)
    {
        if (failure.ResponseStatusCode == 410)
            return true;

        return failure is ODataError { ResponseStatusCode: >= 400 and < 500, Error.Code: { } code }
               && (code.Contains("syncState", StringComparison.OrdinalIgnoreCase)
                   || code.Contains("resync", StringComparison.OrdinalIgnoreCase));
    }
}
