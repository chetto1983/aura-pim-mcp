using MimeKit;

namespace CalendarMcp.Core.Changes;

/// <summary>A <c>From</c> header read into its address and display name.</summary>
internal static class MailSender
{
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
