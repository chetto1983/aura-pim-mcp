using CalendarMcp.Core.Models;
using CalendarMcp.Core.Utilities;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace CalendarMcp.Core.Providers;

public partial class ImapProviderService
{
    private static NotSupportedException Unsupported(string operation) =>
        new($"Provider '{ProviderName}' does not support {operation}. " +
            "IMAP accounts are email-only; pick a different account for this operation.");

    public Task<IEnumerable<CalendarInfo>> ListCalendarsAsync(string accountId, CancellationToken cancellationToken = default) =>
        throw Unsupported("calendar operations");

    public Task<IEnumerable<CalendarEvent>> GetCalendarEventsAsync(
        string accountId, string? calendarId = null, DateTime? startDate = null, DateTime? endDate = null,
        int count = 50, CancellationToken cancellationToken = default) =>
        throw Unsupported("calendar operations");

    public Task<CalendarEvent?> GetCalendarEventDetailsAsync(
        string accountId, string calendarId, string eventId, CancellationToken cancellationToken = default) =>
        throw Unsupported("calendar operations");

    public Task<string> CreateEventAsync(
        string accountId, string? calendarId, string subject, DateTime start, DateTime end,
        string? location = null, List<string>? attendees = null, string? body = null,
        string? timeZone = null, CancellationToken cancellationToken = default) =>
        throw Unsupported("calendar operations");

    public Task UpdateEventAsync(
        string accountId, string calendarId, string eventId, string? subject = null,
        DateTime? start = null, DateTime? end = null, string? location = null,
        List<string>? attendees = null, string? timeZone = null,
        CancellationToken cancellationToken = default) =>
        throw Unsupported("calendar operations");

    public Task DeleteEventAsync(
        string accountId, string calendarId, string eventId, CancellationToken cancellationToken = default) =>
        throw Unsupported("calendar operations");

    public Task RespondToEventAsync(
        string accountId, string calendarId, string eventId, string response,
        string? comment = null, CancellationToken cancellationToken = default) =>
        throw Unsupported("calendar operations");

    public Task<IEnumerable<Contact>> GetContactsAsync(
        string accountId, int count = 50, CancellationToken cancellationToken = default) =>
        throw Unsupported("contact operations");

    public Task<IEnumerable<Contact>> SearchContactsAsync(
        string accountId, string query, int count = 50, CancellationToken cancellationToken = default) =>
        throw Unsupported("contact operations");

    public Task<Contact?> GetContactDetailsAsync(
        string accountId, string contactId, CancellationToken cancellationToken = default) =>
        throw Unsupported("contact operations");

    public Task<string> CreateContactAsync(
        string accountId, string displayName, string? givenName = null, string? surname = null,
        List<string>? emailAddresses = null, List<string>? phoneNumbers = null,
        string? jobTitle = null, string? companyName = null, string? notes = null,
        CancellationToken cancellationToken = default) =>
        throw Unsupported("contact operations");

    public Task UpdateContactAsync(
        string accountId, string contactId, string? displayName = null, string? givenName = null,
        string? surname = null, List<string>? emailAddresses = null, List<string>? phoneNumbers = null,
        string? jobTitle = null, string? companyName = null, string? notes = null,
        string? etag = null, CancellationToken cancellationToken = default) =>
        throw Unsupported("contact operations");

    public Task DeleteContactAsync(
        string accountId, string contactId, CancellationToken cancellationToken = default) =>
        throw Unsupported("contact operations");

    private static EmailMessage SummaryToEmail(
        IMessageSummary s, string folder, uint uidValidity, string accountId)
    {
        var envelope = s.Envelope;
        var from = envelope?.From.Mailboxes.FirstOrDefault();
        var hasAttachments = s.Attachments?.Any() ?? false;

        var listUnsub = HeaderValue(s.Headers, "List-Unsubscribe");
        var listUnsubPost = HeaderValue(s.Headers, "List-Unsubscribe-Post");

        return new EmailMessage
        {
            Id = FormatEmailId(folder, uidValidity, s.UniqueId.Id),
            AccountId = accountId,
            Subject = envelope?.Subject ?? string.Empty,
            From = from?.Address ?? string.Empty,
            FromName = from?.Name ?? string.Empty,
            To = envelope?.To.Mailboxes.Select(a => a.Address).ToList() ?? [],
            Cc = envelope?.Cc.Mailboxes.Select(a => a.Address).ToList() ?? [],
            Body = string.Empty,
            BodyFormat = "text",
            ReceivedDateTime = (s.InternalDate ?? envelope?.Date ?? DateTimeOffset.MinValue).UtcDateTime,
            IsRead = s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
            HasAttachments = hasAttachments,
            UnsubscribeInfo = UnsubscribeHeaderParser.Parse(listUnsub, listUnsubPost)
        };
    }

    private static EmailMessage MessageToEmail(
        MimeMessage m, string folder, uint uidValidity, uint uid, string accountId, bool isRead)
    {
        var from = m.From.Mailboxes.FirstOrDefault();
        var (body, format) = ExtractBody(m);

        var attachments = m.Attachments
            .OfType<MimePart>()
            .Select((p, i) => new EmailAttachment
            {
                Name = p.FileName ?? "(unnamed)",
                Size = p.Content?.Stream?.Length ?? 0,
                ContentType = p.ContentType?.MimeType ?? "application/octet-stream",
                AttachmentId = $"part-{i}",
            })
            .ToList();

        return new EmailMessage
        {
            Id = FormatEmailId(folder, uidValidity, uid),
            AccountId = accountId,
            Subject = m.Subject ?? string.Empty,
            From = from?.Address ?? string.Empty,
            FromName = from?.Name ?? string.Empty,
            To = m.To.Mailboxes.Select(a => a.Address).ToList(),
            Cc = m.Cc.Mailboxes.Select(a => a.Address).ToList(),
            Body = body,
            BodyFormat = format,
            ReceivedDateTime = m.Date.UtcDateTime,
            IsRead = isRead,
            HasAttachments = attachments.Count > 0,
            Attachments = attachments,
            UnsubscribeInfo = UnsubscribeHeaderParser.Parse(
                m.Headers["List-Unsubscribe"],
                m.Headers["List-Unsubscribe-Post"])
        };
    }

    private static (string Body, string Format) ExtractBody(MimeMessage m)
    {
        if (m.HtmlBody is { Length: > 0 } html)
            return (html, "html");
        if (m.TextBody is { Length: > 0 } text)
            return (text, "text");
        return (string.Empty, "text");
    }

    private static string? HeaderValue(HeaderList? headers, string name)
    {
        if (headers is null) return null;
        var header = headers.FirstOrDefault(h => string.Equals(h.Field, name, StringComparison.OrdinalIgnoreCase));
        return header?.Value;
    }

    private static IEnumerable<string> SplitAddresses(string addresses) =>
        addresses.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void EnsureUidValidity(IMailFolder folder, uint expected, string accountId, string folderName)
    {
        if (folder.UidValidity == expected) return;
        throw new InvalidOperationException(
            $"Email ID is no longer valid: folder '{folderName}' on account '{accountId}' " +
            $"has UIDVALIDITY {folder.UidValidity}, but the ID was created with {expected}. " +
            "Re-list the folder to get current IDs.");
    }

    private sealed record ImapAccountConfig(
        string AccountId,
        string ImapHost, int ImapPort,
        string SmtpHost, int SmtpPort,
        string Username, string Password,
        string InboxFolder, string SentFolder, string TrashFolder);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var conn in _imapConnections.Values)
        {
            await conn.Gate.WaitAsync();
            try
            {
                await EvictClientAsync(conn);
            }
            finally
            {
                conn.Gate.Release();
                conn.Gate.Dispose();
            }
        }

        _imapConnections.Clear();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var conn in _imapConnections.Values)
        {
            try { conn.Client?.Dispose(); }
            catch { /* best effort during teardown */ }
            conn.Gate.Dispose();
        }

        _imapConnections.Clear();
        GC.SuppressFinalize(this);
    }
}
