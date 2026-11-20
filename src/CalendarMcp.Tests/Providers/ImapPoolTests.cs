using CalendarMcp.Tests.Changes;
using MailKit.Security;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// The pool keeps one logged-in session per account. A repaired account must be read with what it was
/// repaired to, not with the session the old password opened.
/// </summary>
[TestClass]
public class ImapPoolTests
{
    [TestMethod]
    public async Task Pool_ConfigChange_ReopensWithTheNewConfig()
    {
        await using var mailbox = await ImapMailbox.OpenAsync(ImapServers.Dovecot);
        var cursor = await mailbox.StartAsync();

        mailbox.Account.ProviderConfig["password"] = "wrong-" + ImapMailbox.Password;

        await Assert.ThrowsAsync<AuthenticationException>(() => mailbox.ReadAsync(cursor));
    }
}
