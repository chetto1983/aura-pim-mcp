using CalendarMcp.Core.Utilities;
using MimeKit;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// Nesting is free for a sender, so a mail with its words past what is walked must not read as a mail
/// with nothing to say. On real servers, because what a server does with depth is its own business.
/// </summary>
[TestClass]
public class ImapDeepMimeTests
{
    private const string Words = "Parole della mail";

    private static MimeMessage Nested(int levels)
    {
        MimeEntity body = new TextPart("plain") { Text = Words };
        for (var i = 0; i < levels; i++)
            body = new Multipart("mixed") { body };

        var message = ImapMailbox.Mail();
        message.Body = body;
        return message;
    }

    private static async Task ReadsAsync((string Host, int Port) server, int levels, string body, string format, string snippet)
    {
        await using var mailbox = await ImapMailbox.OpenAsync(server);
        var cursor = await mailbox.StartAsync();

        await mailbox.DeliverAsync(Nested(levels));

        var change = (await mailbox.ReadAsync(cursor)).Changes.Single();
        var email = await mailbox.Provider.GetEmailDetailsAsync(mailbox.Account.Id, change.Id);

        Assert.AreEqual(body, email!.Body.Trim(), $"body at {levels} levels on {server}");
        Assert.AreEqual(format, email.BodyFormat, $"format at {levels} levels on {server}");
        Assert.AreEqual(snippet, change.Snippet, $"snippet at {levels} levels on {server}");
    }

    [TestMethod]
    [DataRow(1, Words, "text", Words)]
    [DataRow(32, Words, "text", Words)]
    [DataRow(33, MailBody.TooDeep, MailBody.OmittedFormat, "")]
    [DataRow(50, MailBody.TooDeep, MailBody.OmittedFormat, "")]
    [DataRow(1100, MailBody.TooDeep, MailBody.OmittedFormat, "")]
    public async Task AMailNestedPastReading_SaysItsBodyIsLeftOut(int levels, string body, string format, string snippet)
    {
        foreach (var server in new[] { ImapServers.GreenMail, ImapServers.Dovecot })
            await ReadsAsync(server, levels, body, format, snippet);
    }

    /// <summary>Dovecot's alone: GreenMail overflows its own stack in <c>parseBodyStructure</c> here and drops the session.</summary>
    [TestMethod]
    public Task AMailNestedThousandsDeep_SaysItsBodyIsLeftOut() =>
        ReadsAsync(ImapServers.Dovecot, 5000, MailBody.TooDeep, MailBody.OmittedFormat, string.Empty);
}
