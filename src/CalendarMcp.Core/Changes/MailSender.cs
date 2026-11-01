using System.Text.RegularExpressions;
using MimeKit;

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

    /// <summary>The first mailbox in the header. Text that holds none comes back as the name.</summary>
    public static (string? Address, string? Name) Read(string? from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return (null, null);

        if (InternetAddressList.TryParse(from, out var addresses) && addresses.Mailboxes.FirstOrDefault() is { } mailbox)
            return (mailbox.Address, string.IsNullOrEmpty(mailbox.Name) ? null : mailbox.Name);

        return (null, from);
    }
}
