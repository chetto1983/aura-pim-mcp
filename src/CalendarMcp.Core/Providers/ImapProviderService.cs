using CalendarMcp.Core.Models;
using CalendarMcp.Core.Security;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Utilities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Text;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Provider service for any IMAP/SMTP-accessible mailbox. Email-only — calendar and
/// contact methods throw <see cref="NotSupportedException"/>. Defaults are tuned for
/// Gmail (host names, sent/trash folders) but every value can be overridden per
/// account, so the provider works for any IMAP host.
/// </summary>
public partial class ImapProviderService : IImapProviderService, IAsyncDisposable, IDisposable
{
    public const string DefaultImapHost = "imap.gmail.com";
    public const int DefaultImapPort = 993;
    public const string DefaultSmtpHost = "smtp.gmail.com";
    public const int DefaultSmtpPort = 587;
    public const string DefaultInbox = "INBOX";
    public const string DefaultSent = "[Gmail]/Sent Mail";
    public const string DefaultTrash = "[Gmail]/Trash";

    private const string ProviderName = "imap";

    private const MessageSummaryItems EnvelopeItems =
        MessageSummaryItems.Envelope |
        MessageSummaryItems.Flags |
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.BodyStructure |
        MessageSummaryItems.InternalDate |
        MessageSummaryItems.Headers;

    private readonly IAccountRegistry _accountRegistry;
    private readonly PasswordProtector _passwordProtector;
    private readonly ILogger<ImapProviderService> _logger;

    // Gmail (and most IMAP hosts) cap the number of simultaneous connections per
    // account and aggressively throttle repeated AUTHENTICATE attempts. Opening a
    // fresh, separately-authenticated connection on every call — and closing it
    // abruptly via Dispose() without a graceful LOGOUT — leaves connection slots
    // lingering server-side, so a burst of calls exhausts the limit and stalls the
    // next AUTHENTICATE. Instead we keep one authenticated connection per account,
    // reuse it across calls, and dispose it gracefully. A per-account gate
    // serializes access because a single ImapClient can only run one command at a time.
    private readonly ConcurrentDictionary<string, PooledImapConnection> _imapConnections = new();
    private volatile bool _disposed;

    public ImapProviderService(
        IAccountRegistry accountRegistry,
        PasswordProtector passwordProtector,
        ILogger<ImapProviderService> logger)
    {
        _accountRegistry = accountRegistry;
        _passwordProtector = passwordProtector;
        _logger = logger;
    }

    // ── ID encoding ──────────────────────────────────────────────────

    /// <summary>
    /// Encodes a stable, portable email ID as <c>folder/uidvalidity/uid</c>. Folder names
    /// containing literal slashes (Gmail uses <c>[Gmail]/Trash</c> etc.) are preserved as-is —
    /// parsing splits on the last two slashes so internal slashes inside the folder name
    /// don't confuse it.
    /// </summary>
    public static string FormatEmailId(string folder, uint uidValidity, uint uid) =>
        $"{folder}/{uidValidity}/{uid}";

    /// <summary>
    /// Parses an ID created by <see cref="FormatEmailId"/>. Throws if the format is invalid —
    /// callers must surface a user-facing error rather than silently misinterpret.
    /// </summary>
    public static (string Folder, uint UidValidity, uint Uid) ParseEmailId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Email ID is required.", nameof(id));

        var lastSlash = id.LastIndexOf('/');
        if (lastSlash <= 0)
            throw new FormatException(
                $"Email ID '{id}' is not in IMAP format. Expected '<folder>/<uidvalidity>/<uid>'.");

        var secondLastSlash = id.LastIndexOf('/', lastSlash - 1);
        if (secondLastSlash <= 0)
            throw new FormatException(
                $"Email ID '{id}' is not in IMAP format. Expected '<folder>/<uidvalidity>/<uid>'.");

        var folder = id[..secondLastSlash];
        if (!uint.TryParse(id[(secondLastSlash + 1)..lastSlash], out var uidValidity) ||
            !uint.TryParse(id[(lastSlash + 1)..], out var uid))
        {
            throw new FormatException(
                $"Email ID '{id}' is not in IMAP format. UID validity and UID must be unsigned integers.");
        }

        return (folder, uidValidity, uid);
    }

    // ── Account configuration ────────────────────────────────────────

    private async Task<ImapAccountConfig> ResolveConfigAsync(string accountId)
    {
        var account = await _accountRegistry.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' not found in registry.");

        var pc = account.ProviderConfig;

        var imapHost = Get(pc, "imapHost", DefaultImapHost);
        var smtpHost = Get(pc, "smtpHost", DefaultSmtpHost);
        var username = Get(pc, "username", "");
        var storedPassword = Get(pc, "password", "");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(storedPassword))
            throw new InvalidOperationException(
                $"Account '{accountId}' is missing username or password in providerConfig.");

        return new ImapAccountConfig(
            AccountId: accountId,
            ImapHost: imapHost,
            ImapPort: GetInt(pc, "imapPort", DefaultImapPort),
            SmtpHost: smtpHost,
            SmtpPort: GetInt(pc, "smtpPort", DefaultSmtpPort),
            Username: username,
            Password: _passwordProtector.Unprotect(storedPassword),
            InboxFolder: Get(pc, "inboxFolder", DefaultInbox),
            SentFolder: Get(pc, "sentFolder", DefaultSent),
            TrashFolder: Get(pc, "trashFolder", DefaultTrash));

        static string Get(IDictionary<string, string> d, string key, string fallback) =>
            d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        static int GetInt(IDictionary<string, string> d, string key, int fallback) =>
            d.TryGetValue(key, out var v) && int.TryParse(v, out var n) && n > 0 ? n : fallback;
    }

    private async Task<ImapClient> OpenImapAsync(ImapAccountConfig cfg, CancellationToken ct)
    {
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(cfg.ImapHost, cfg.ImapPort, SecureSocketOptions.Auto, ct);
            await client.AuthenticateAsync(cfg.Username, cfg.Password, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<SmtpClient> OpenSmtpAsync(ImapAccountConfig cfg, CancellationToken ct)
    {
        var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(cfg.SmtpHost, cfg.SmtpPort, SecureSocketOptions.Auto, ct);
            if (client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                await client.AuthenticateAsync(cfg.Username, cfg.Password, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<IMailFolder> OpenFolderAsync(
        ImapClient client, string folderName, FolderAccess access, CancellationToken ct)
    {
        var folder = string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(folderName, ct);

        if (folder is null)
            throw new InvalidOperationException($"IMAP folder '{folderName}' not found.");

        await folder.OpenAsync(access, ct);
        return folder;
    }

    // ── Connection pooling ───────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="action"/> against a reused, authenticated IMAP connection
    /// for the account. The connection is health-checked (NOOP) before use and
    /// reconnected if dead; a broken connection mid-operation triggers a single
    /// reconnect-and-retry. The connection is kept open for subsequent calls rather
    /// than disposed, avoiding repeated AUTHENTICATE round-trips that IMAP hosts throttle.
    /// </summary>
    private async Task<T> WithImapAsync<T>(
        ImapAccountConfig cfg, Func<ImapClient, Task<T>> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var conn = _imapConnections.GetOrAdd(cfg.AccountId, _ => new PooledImapConnection());
        await conn.Gate.WaitAsync(ct);
        try
        {
            var (client, reused) = await AcquireClientAsync(conn, cfg, ct);
            try
            {
                return await action(client);
            }
            catch (Exception ex) when (reused && IsConnectionException(ex) && !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex,
                    "Pooled IMAP connection for {AccountId} broke mid-operation; reconnecting and retrying once.",
                    cfg.AccountId);
                await EvictClientAsync(conn);
                var (retryClient, _) = await AcquireClientAsync(conn, cfg, ct);
                return await action(retryClient);
            }
            catch (Exception ex) when (IsConnectionException(ex))
            {
                await EvictClientAsync(conn);
                throw;
            }
        }
        finally
        {
            conn.Gate.Release();
        }
    }

    private Task WithImapAsync(ImapAccountConfig cfg, Func<ImapClient, Task> action, CancellationToken ct) =>
        WithImapAsync(cfg, async client =>
        {
            await action(client);
            return true;
        }, ct);

    /// <summary>
    /// Returns the account's cached connection if it is still alive (verified with a
    /// lightweight NOOP), otherwise opens and caches a fresh authenticated connection.
    /// The boolean indicates whether an existing connection was reused.
    /// </summary>
    private async Task<(ImapClient Client, bool Reused)> AcquireClientAsync(
        PooledImapConnection conn, ImapAccountConfig cfg, CancellationToken ct)
    {
        var existing = conn.Client;
        if (existing is { IsConnected: true, IsAuthenticated: true })
        {
            try
            {
                await existing.NoOpAsync(ct);
                return (existing, true);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex,
                    "Cached IMAP connection for {AccountId} failed health check; reconnecting.", cfg.AccountId);
                await EvictClientAsync(conn);
            }
        }
        else if (existing is not null)
        {
            await EvictClientAsync(conn);
        }

        var fresh = await OpenImapAsync(cfg, ct);
        conn.Client = fresh;
        return (fresh, false);
    }

    /// <summary>
    /// Gracefully logs out (so the server releases the connection slot immediately)
    /// and disposes the cached connection. Always uses <see cref="CancellationToken.None"/>
    /// for the LOGOUT so a cancelled operation still disconnects cleanly.
    /// </summary>
    private async Task EvictClientAsync(PooledImapConnection conn)
    {
        var client = conn.Client;
        conn.Client = null;
        if (client is null) return;

        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during graceful IMAP disconnect; disposing anyway.");
        }
        finally
        {
            client.Dispose();
        }
    }

    private static bool IsConnectionException(Exception ex) =>
        ex is ImapProtocolException
            or IOException
            or SocketException
            or ServiceNotConnectedException;

    private sealed class PooledImapConnection
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ImapClient? Client { get; set; }
    }

    // ── Email operations ─────────────────────────────────────────────

    public async Task<IEnumerable<EmailMessage>> GetEmailsAsync(
        string accountId, int count = 20, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        return await WithImapAsync<IEnumerable<EmailMessage>>(cfg, async client =>
        {
            var folder = await OpenFolderAsync(client, cfg.InboxFolder, FolderAccess.ReadOnly, cancellationToken);

            IList<UniqueId> uids;
            if (unreadOnly)
            {
                uids = await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken);
            }
            else
            {
                uids = (await folder.SearchAsync(SearchQuery.All, cancellationToken)).ToList();
            }

            var newest = uids.OrderByDescending(u => u.Id).Take(count).ToList();
            if (newest.Count == 0)
                return [];

            var summaries = await folder.FetchAsync(newest, EnvelopeItems, cancellationToken);

            return summaries
                .OrderByDescending(s => s.InternalDate ?? s.Date)
                .Select(s => SummaryToEmail(s, cfg.InboxFolder, folder.UidValidity, accountId))
                .ToList();
        }, cancellationToken);
    }

    public async Task<IEnumerable<EmailMessage>> SearchEmailsAsync(
        string accountId, string query, int count = 20,
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        return await WithImapAsync<IEnumerable<EmailMessage>>(cfg, async client =>
        {
            var folder = await OpenFolderAsync(client, cfg.InboxFolder, FolderAccess.ReadOnly, cancellationToken);

            SearchQuery search = string.IsNullOrWhiteSpace(query)
                ? SearchQuery.All
                : SearchQuery.SubjectContains(query)
                    .Or(SearchQuery.BodyContains(query))
                    .Or(SearchQuery.FromContains(query));

            if (fromDate.HasValue)
                search = search.And(SearchQuery.DeliveredAfter(fromDate.Value));
            if (toDate.HasValue)
                search = search.And(SearchQuery.DeliveredBefore(toDate.Value));

            var uids = (await folder.SearchAsync(search, cancellationToken))
                .OrderByDescending(u => u.Id)
                .Take(count)
                .ToList();

            if (uids.Count == 0)
                return [];

            var summaries = await folder.FetchAsync(uids, EnvelopeItems, cancellationToken);

            return summaries
                .OrderByDescending(s => s.InternalDate ?? s.Date)
                .Select(s => SummaryToEmail(s, cfg.InboxFolder, folder.UidValidity, accountId))
                .ToList();
        }, cancellationToken);
    }

    public async Task<EmailMessage?> GetEmailDetailsAsync(
        string accountId, string emailId, CancellationToken cancellationToken = default)
    {
        var (folderName, uidValidity, uid) = ParseEmailId(emailId);
        var cfg = await ResolveConfigAsync(accountId);

        return await WithImapAsync<EmailMessage?>(cfg, async client =>
        {
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadOnly, cancellationToken);

            if (folder.UidValidity != uidValidity)
            {
                _logger.LogWarning(
                    "UIDVALIDITY mismatch for {AccountId} folder {Folder}: id={Stored}, current={Current}. Message no longer addressable.",
                    accountId, folderName, uidValidity, folder.UidValidity);
                return null;
            }

            var message = await folder.GetMessageAsync(new UniqueId(uid), cancellationToken);
            if (message is null) return null;

            var summary = (await folder.FetchAsync(new[] { new UniqueId(uid) }, EnvelopeItems, cancellationToken))
                .FirstOrDefault();
            var isRead = summary?.Flags?.HasFlag(MessageFlags.Seen) ?? true;

            return MessageToEmail(message, folderName, uidValidity, uid, accountId, isRead);
        }, cancellationToken);
    }

    public async Task<EmailAttachmentContent?> GetEmailAttachmentContentAsync(
        string accountId, string emailId, string attachmentId,
        CancellationToken cancellationToken = default)
    {
        // IMAP attachment ids are positional: "part-N".
        if (!attachmentId.StartsWith("part-", StringComparison.Ordinal)
            || !int.TryParse(attachmentId.AsSpan(5), out var index)
            || index < 0)
        {
            _logger.LogWarning("Invalid IMAP attachment id {AttachmentId}", attachmentId);
            return null;
        }

        var (folderName, uidValidity, uid) = ParseEmailId(emailId);
        var cfg = await ResolveConfigAsync(accountId);

        return await WithImapAsync<EmailAttachmentContent?>(cfg, async client =>
        {
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadOnly, cancellationToken);

            if (folder.UidValidity != uidValidity)
            {
                _logger.LogWarning("UIDVALIDITY mismatch fetching attachment for {AccountId} {Folder}", accountId, folderName);
                return null;
            }

            var message = await folder.GetMessageAsync(new UniqueId(uid), cancellationToken);
            if (message is null) return null;

            var parts = message.Attachments.OfType<MimePart>().ToList();
            if (index >= parts.Count)
            {
                _logger.LogWarning("Attachment index {Index} out of range (have {Count})", index, parts.Count);
                return null;
            }

            var part = parts[index];
            if (part.Content is null)
            {
                _logger.LogWarning("IMAP attachment {AttachmentId} has no content", attachmentId);
                return null;
            }

            using var ms = new MemoryStream();
            await part.Content.DecodeToAsync(ms, cancellationToken);

            return new EmailAttachmentContent
            {
                Name = part.FileName ?? "attachment",
                ContentType = part.ContentType?.MimeType,
                Bytes = ms.ToArray(),
            };
        }, cancellationToken);
    }

    public async Task<string> SendEmailAsync(
        string accountId, string to, string subject, string body,
        string bodyFormat = "html", List<string>? cc = null,
        IReadOnlyList<OutboundEmailAttachment>? attachments = null,
        string? textBody = null,
        string? htmlBody = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(cfg.Username));
        foreach (var addr in SplitAddresses(to))
            message.To.Add(MailboxAddress.Parse(addr));
        if (cc is { Count: > 0 })
        {
            foreach (var addr in cc)
                message.Cc.Add(MailboxAddress.Parse(addr));
        }
        message.Subject = subject;

        if (attachments is { Count: > 0 } || bodyFormat.Equals("multipart", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new BodyBuilder();
            if (bodyFormat.Equals("multipart", StringComparison.OrdinalIgnoreCase))
            {
                builder.TextBody = textBody;
                builder.HtmlBody = htmlBody;
            }
            else if (bodyFormat?.Equals("text", StringComparison.OrdinalIgnoreCase) == true)
                builder.TextBody = body;
            else
                builder.HtmlBody = body;

            if (attachments is { Count: > 0 })
            {
                foreach (var att in attachments)
                    MimeAttachmentBuilder.Add(builder, att);
            }

            message.Body = builder.ToMessageBody();
        }
        else
        {
            message.Body = new TextPart(bodyFormat?.Equals("text", StringComparison.OrdinalIgnoreCase) == true
                ? TextFormat.Plain
                : TextFormat.Html)
            { Text = body };
        }

        using (var smtp = await OpenSmtpAsync(cfg, cancellationToken))
        {
            await smtp.SendAsync(message, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }

        // APPEND to sent folder so the message shows up in the user's Sent box.
        // Failure here is non-fatal — the message did go out.
        try
        {
            await WithImapAsync(cfg, async imap =>
            {
                var sent = await imap.GetFolderAsync(cfg.SentFolder, cancellationToken);
                await sent.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
                await sent.AppendAsync(message, MessageFlags.Seen, DateTimeOffset.Now, cancellationToken);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to APPEND sent message to {Folder} for account {AccountId}; message was still delivered.",
                cfg.SentFolder, accountId);
        }

        return message.MessageId ?? string.Empty;
    }

    public async Task DeleteEmailAsync(
        string accountId, string emailId, CancellationToken cancellationToken = default)
    {
        var (folderName, uidValidity, uid) = ParseEmailId(emailId);
        var cfg = await ResolveConfigAsync(accountId);

        await WithImapAsync(cfg, async client =>
        {
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadWrite, cancellationToken);
            EnsureUidValidity(folder, uidValidity, accountId, folderName);

            var trash = await client.GetFolderAsync(cfg.TrashFolder, cancellationToken);
            await folder.MoveToAsync(new[] { new UniqueId(uid) }, trash, cancellationToken);
        }, cancellationToken);
    }

    public async Task MarkEmailAsReadAsync(
        string accountId, string emailId, bool isRead, CancellationToken cancellationToken = default)
    {
        var (folderName, uidValidity, uid) = ParseEmailId(emailId);
        var cfg = await ResolveConfigAsync(accountId);

        await WithImapAsync(cfg, async client =>
        {
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadWrite, cancellationToken);
            EnsureUidValidity(folder, uidValidity, accountId, folderName);

            var ids = new[] { new UniqueId(uid) };
            if (isRead)
                await folder.AddFlagsAsync(ids, MessageFlags.Seen, true, cancellationToken);
            else
                await folder.RemoveFlagsAsync(ids, MessageFlags.Seen, true, cancellationToken);
        }, cancellationToken);
    }

    public async Task MoveEmailAsync(
        string accountId, string emailId, string destinationFolder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
            throw new ArgumentException("Destination folder is required.", nameof(destinationFolder));

        var (folderName, uidValidity, uid) = ParseEmailId(emailId);
        var cfg = await ResolveConfigAsync(accountId);

        await WithImapAsync(cfg, async client =>
        {
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadWrite, cancellationToken);
            EnsureUidValidity(folder, uidValidity, accountId, folderName);

            var dest = await client.GetFolderAsync(destinationFolder, cancellationToken);
            await folder.MoveToAsync(new[] { new UniqueId(uid) }, dest, cancellationToken);
        }, cancellationToken);
    }
}
