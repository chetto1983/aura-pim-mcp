using System.Text.RegularExpressions;
using MimeKit;

namespace CalendarMcp.Core.Changes;

/// <summary>
/// What a mail's headers say, for the providers that hand over headers. Each provider decides how
/// to reach a header, so these take a lookup. Names are matched case-insensitively by that lookup.
/// </summary>
internal static partial class MailHeaderSignals
{
    /// <summary>List mail by its standard headers. A signal, never a verdict: CI alerts carry them too.</summary>
    public static bool IsBulk(Func<string, string?> header) =>
        header("List-Id") is not null || header("List-Unsubscribe") is not null;

    /// <summary>A reply by its threading headers, the only mail whose thread can hold one the account sent.</summary>
    public static bool IsReply(Func<string, string?> header) =>
        header("In-Reply-To") is not null || header("References") is not null;

    /// <summary>The sender's own claim of importance, which is all that IMAP has.</summary>
    public static bool IsImportant(Func<string, string?> header)
    {
        if (string.Equals(header("Importance")?.Trim(), "high", StringComparison.OrdinalIgnoreCase))
            return true;

        return header("X-Priority")?.TrimStart() is ['1' or '2', ..];
    }

    /// <summary>
    /// Whether a part's Content-Type is a scheduling message. RFC 6047 section 2.4 requires the
    /// <c>method</c> parameter, and a <c>text/calendar</c> part without it is a calendar file. PUBLISH
    /// expects no answer and reaches no calendar by itself, which is a ticket or a booking.
    /// </summary>
    public static bool IsScheduling(string? contentType) =>
        ContentType.TryParse(contentType ?? string.Empty, out var type) && IsScheduling(type);

    /// <inheritdoc cref="IsScheduling(string?)"/>
    public static bool IsScheduling(ContentType type) =>
        type.IsMimeType("text", "calendar")
        && type.Parameters["method"] is { Length: > 0 } method
        && !method.Equals("PUBLISH", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the receiving server's verdict is a failure. <paramref name="topmostResults"/> must be the
    /// topmost <c>Authentication-Results</c> header: RFC 8601 section 7 has it prepended on arrival, so any
    /// below it could have been written by the sender. DMARC decides when it answered, because it
    /// already weighs SPF and DKIM together. No header at all is not a failure: it says nothing.
    /// </summary>
    public static bool AuthenticationFailed(string? topmostResults)
    {
        if (string.IsNullOrWhiteSpace(topmostResults))
            return false;

        var results = ReadResults(topmostResults);

        if (results.TryGetValue("dmarc", out var dmarc) && dmarc is "pass" or "bestguesspass")
            return false;
        if (dmarc == "fail" || results.GetValueOrDefault("compauth") == "fail")
            return true;

        var spf = results.GetValueOrDefault("spf");
        var dkim = results.GetValueOrDefault("dkim");
        return (spf == "fail" && dkim != "pass") || (dkim == "fail" && spf != "pass");
    }

    // Comments and quoted strings can hold "dmarc=fail" as free text from the message.
    private static Dictionary<string, string> ReadResults(string header)
    {
        var bare = QuotedString().Replace(Comment().Replace(header, " "), " ");
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var clause in bare.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = MethodResult().Match(clause);
            if (match.Success)
                results.TryAdd(match.Groups["method"].Value, match.Groups["result"].Value.ToLowerInvariant());
        }

        return results;
    }

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex Comment();

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex QuotedString();

    [GeneratedRegex(@"^\s*(?<method>[A-Za-z0-9-]+)\s*=\s*(?<result>[A-Za-z0-9-]+)")]
    private static partial Regex MethodResult();
}
