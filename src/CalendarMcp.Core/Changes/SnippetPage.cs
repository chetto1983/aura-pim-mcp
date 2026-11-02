using CalendarMcp.Core.Utilities;

namespace CalendarMcp.Core.Changes;

/// <summary>
/// What one page may spend on snippets. At 500 ms each, the 200 mails a page can hold outlast the 60 s
/// the caller waits, and a page that times out is read again with the same mails forever. Once this is
/// spent the rest of the page has empty snippets and comes back as usual, so no cursor has to know.
/// </summary>
internal sealed class SnippetPage(TimeProvider clock, CancellationToken caller)
{
    public static readonly TimeSpan Allowed = TimeSpan.FromSeconds(30);

    private TimeSpan _spent;

    /// <summary>The mails this page had no time left for. A mail that failed by itself is not one of them.</summary>
    public int Skipped { get; private set; }

    /// <summary>The snippet of a mail. Reading the body happens in here too, so that its failure is the mail's own.</summary>
    public string Of(Func<(string Body, string Format)> read)
    {
        var left = Allowed - _spent;
        if (left <= TimeSpan.Zero)
        {
            Skipped++;
            return string.Empty;
        }

        if (!MailBody.TryWarmUp())
            return string.Empty;

        var started = clock.GetTimestamp();
        var budget = new ConversionBudget(left < MailBody.SnippetBudget ? left : MailBody.SnippetBudget, caller);

        var snippet = MailSnippet.Of(read, budget);

        _spent += clock.GetElapsedTime(started);
        return snippet;
    }
}
