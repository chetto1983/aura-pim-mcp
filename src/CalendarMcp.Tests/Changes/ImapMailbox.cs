using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Security;
using CalendarMcp.Tests.Helpers;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// A real IMAP server for the changes tests, which IMAP's own variety makes worth the container.
/// The variable names a <c>host:port</c>. Locally an unset one leaves the test inconclusive; under
/// <c>CI</c> it fails, so a pipeline that forgot its server cannot pass by exercising nothing.
/// </summary>
internal static class ImapServers
{
    public const string GreenMailVariable = "PIM_TEST_GREENMAIL";
    public const string DovecotVariable = "PIM_TEST_DOVECOT";

    public static (string Host, int Port) GreenMail => Require(GreenMailVariable);

    public static (string Host, int Port) Dovecot => Require(DovecotVariable);

    private static (string Host, int Port) Require(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value))
        {
            var why = $"{variable} is not set; see the header of .github/workflows/ci.yml for how to run this server.";
            if (Environment.GetEnvironmentVariable("CI") is not null)
                Assert.Fail(why);
            Assert.Inconclusive(why);
        }

        var (host, port) = (value![..value.LastIndexOf(':')], value[(value.LastIndexOf(':') + 1)..]);
        return (host, int.Parse(port));
    }
}

/// <summary>
/// One disposable mailbox on a test server, with a second connection acting as the outside world:
/// delivering, reading and deleting mail the way another mail client would. Every mailbox is a new
/// invented user, so tests share nothing.
/// </summary>
internal sealed class ImapMailbox : IAsyncDisposable
{
    public const string Password = "secret";
    public const string SentFolder = "Sent";

    private readonly ImapClient _world = new();

    private ImapMailbox(AccountInfo account, string inbox)
    {
        Account = account;
        Inbox = inbox;
        Provider = new ImapProviderService(
            new SingleAccountRegistry(account),
            new PasswordProtector(new EphemeralDataProtectionProvider()),
            NullLogger<ImapProviderService>.Instance);
    }

    public AccountInfo Account { get; }

    public string Inbox { get; }

    public ImapProviderService Provider { get; }

    public static async Task<ImapMailbox> OpenAsync((string Host, int Port) server, string inbox = "INBOX", bool withSent = true)
    {
        var user = $"{Guid.NewGuid():N}@example.com";
        var account = TestData.CreateAccount(id: user, provider: "imap", providerConfig: new Dictionary<string, string>
        {
            ["imapHost"] = server.Host,
            ["imapPort"] = server.Port.ToString(),
            ["smtpHost"] = server.Host,
            ["username"] = user,
            ["password"] = Password,
            ["inboxFolder"] = inbox,
            ["sentFolder"] = SentFolder
        });

        var mailbox = new ImapMailbox(account, inbox);
        await mailbox._world.ConnectAsync(server.Host, server.Port, SecureSocketOptions.None);
        await mailbox._world.AuthenticateAsync(user, Password);
        if (withSent)
            await mailbox.CreateFolderAsync(SentFolder);
        if (inbox != "INBOX")
            await mailbox.CreateFolderAsync(inbox);

        return mailbox;
    }

    public async Task CreateFolderAsync(string name)
    {
        var root = _world.GetFolder(_world.PersonalNamespaces[0]);
        await root.CreateAsync(name, true);
    }

    private async Task<IMailFolder> FolderAsync(string name) =>
        name == "INBOX" ? _world.Inbox : await _world.GetFolderAsync(name);

    private async Task<IMailFolder> OpenAsync(string name, FolderAccess access)
    {
        var folder = await FolderAsync(name);
        await folder.OpenAsync(access);
        return folder;
    }

    public static MimeMessage Mail(
        string subject = "Fattura", string body = "Testo della mail", string from = "mario@example.com", string? to = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Mario Rossi", from));
        message.To.Add(MailboxAddress.Parse(to ?? "io@example.com"));
        message.Subject = subject;
        message.Date = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    /// <summary>Puts a mail into the watched folder and answers its UID.</summary>
    public Task<uint> DeliverAsync(MimeMessage message) => AppendAsync(Inbox, message);

    public Task<uint> SendAsync(MimeMessage message) => AppendAsync(SentFolder, message);

    public async Task<uint> AppendAsync(string name, MimeMessage message)
    {
        var folder = await OpenAsync(name, FolderAccess.ReadWrite);
        await folder.AppendAsync(new AppendRequest(message));

        return (await folder.SearchAsync(MailKit.Search.SearchQuery.All)).Max(uid => uid.Id);
    }

    /// <summary>Puts a mail into a folder without selecting it, so no session has taken its \Recent yet.</summary>
    public async Task DropAsync(string name, MimeMessage message) =>
        await (await FolderAsync(name)).AppendAsync(new AppendRequest(message));

    /// <summary>How many mails still carry \Recent. STATUS leaves the flag alone (RFC 3501 section 6.3.10).</summary>
    public async Task<int> RecentInAsync(string name)
    {
        var folder = await FolderAsync(name);
        await folder.StatusAsync(StatusItems.Recent);
        return folder.Recent;
    }

    /// <summary>Fills the watched folder in one MULTIAPPEND and answers the UIDs, oldest first.</summary>
    public async Task<List<uint>> DeliverManyAsync(int count)
    {
        var folder = await OpenAsync(Inbox, FolderAccess.ReadWrite);
        var requests = Enumerable.Range(0, count)
            .Select(i => (IAppendRequest)new AppendRequest(Mail($"Mail {i}")))
            .ToList();

        await folder.AppendAsync(requests);

        return (await folder.SearchAsync(MailKit.Search.SearchQuery.All)).Select(uid => uid.Id).Order().ToList();
    }

    public async Task ExpungeAsync(uint uid)
    {
        var folder = await OpenAsync(Inbox, FolderAccess.ReadWrite);
        await folder.StoreAsync([new UniqueId(uid)], new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true });
        await folder.ExpungeAsync();
    }

    public async Task MarkReadAsync(params uint[] uids)
    {
        var folder = await OpenAsync(Inbox, FolderAccess.ReadWrite);
        await folder.StoreAsync(
            uids.Select(uid => new UniqueId(uid)).ToList(),
            new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true });
    }

    public async Task<MessageFlags> FlagsOfAsync(uint uid)
    {
        var folder = await OpenAsync(Inbox, FolderAccess.ReadOnly);
        var summary = await folder.FetchAsync([new UniqueId(uid)], MessageSummaryItems.Flags);
        return summary.Single().Flags ?? MessageFlags.None;
    }

    /// <summary>Both servers derive UIDVALIDITY from the clock in seconds, so the wait is what changes it.</summary>
    public async Task RecreateWatchedFolderAsync()
    {
        var folder = await _world.GetFolderAsync(Inbox);
        await folder.DeleteAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await CreateFolderAsync(Inbox);
    }

    public async Task DeleteWatchedFolderAsync() => await (await _world.GetFolderAsync(Inbox)).DeleteAsync();

    public Task<ChangesCursor> StartAsync() => Provider.StartAsync(Account.Id, default);

    public Task<CalendarMcp.Core.Services.ChangesBatch> ReadAsync(ChangesCursor cursor, int limit = 50) =>
        Provider.ReadAsync(Account.Id, cursor, limit, default);

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        if (_world.IsConnected)
            await _world.DisconnectAsync(true);
        _world.Dispose();
    }
}
