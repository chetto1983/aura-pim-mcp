using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;

namespace CalendarMcp.Core.Changes;

/// <summary>A <c>From</c> header read into its address and display name.</summary>
internal static partial class MailSender
{
    /// <summary>
    /// Whether an address can go into a provider's search with no quoting. A sender is attacker
    /// text, and one that fails this is never searched for.
    /// </summary>
    public static bool IsPlainAddress(string address) => PlainAddress().IsMatch(address);

    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+$")]
    private static partial Regex PlainAddress();

    public static DateTimeOffset? ReadDate(string? date) =>
        DateUtils.TryParse(date, out var parsed) ? parsed : null;

    /// <summary>The first mailbox in the header. Text that holds none comes back as the name.</summary>
    public static (string? Address, string? Name) Read(string? from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return (null, null);

        if (InternetAddressList.TryParse(from, out var addresses) && addresses.Mailboxes.FirstOrDefault() is { } mailbox)
            return (mailbox.Address, string.IsNullOrEmpty(mailbox.Name) ? null : mailbox.Name);

        return (null, from);
    }

    /// <summary>
    /// A mailbox from an IMAP envelope. A From with no address arrives as the server's placeholder,
    /// which names nobody, so it reads as the name alone, as a header with no mailbox does.
    /// </summary>
    public static (string? Address, string? Name) ReadEnvelope(MailboxAddress? mailbox)
    {
        var name = string.IsNullOrEmpty(mailbox?.Name) ? null : mailbox.Name;
        return (mailbox?.Address == MissingMailbox ? null : mailbox?.Address, name);
    }

    private const string MissingMailbox = "MISSING_MAILBOX";
}
