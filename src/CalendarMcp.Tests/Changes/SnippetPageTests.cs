using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Utilities;
using CalendarMcp.Tests.Helpers;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// A page of slow mails must still come back inside the 60 s Aura waits. What runs out is the
/// snippets and never the page, because every adapter's cursor already moves past what it returned.
/// </summary>
[TestClass]
public class SnippetPageTests
{
    /// <summary>The clock moves once before a conversion and once after, so each takes a third of the page.</summary>
    private static readonly TimeSpan AThirdOfThePage = SnippetPage.Allowed / 3;

    private static readonly string[] ThreeOfFive = ["Parole", "Parole", "Parole", string.Empty, string.Empty];

    private static (string, string) Html() => ("<p>Parole</p>", MailBody.HtmlFormat);

    [TestMethod]
    public void ThePageRunningOut_LeavesTheRestEmpty()
    {
        var page = new SnippetPage(new SteppingClock(AThirdOfThePage), default);

        var snippets = Enumerable.Range(0, 5).Select(_ => page.Of(Html)).ToArray();

        CollectionAssert.AreEqual(ThreeOfFive, snippets);
        Assert.AreEqual(2, page.Skipped);
    }

    [TestMethod]
    public void WhatIsLeft_IsAllTheNextConversionGets()
    {
        var page = new SnippetPage(new SteppingClock(SnippetPage.Allowed - TimeSpan.FromTicks(1)), default);

        Assert.AreEqual("Parole", page.Of(Html));
        Assert.AreEqual(string.Empty, page.Of(Html), "a tick is no time to convert in");
    }

    [TestMethod]
    public void ABodyThatCannotBeRead_LosesOnlyItsOwnSnippet()
    {
        var page = new SnippetPage(TimeProvider.System, default);

        Assert.AreEqual(string.Empty, page.Of(() => throw new FormatException("not base64")));
        Assert.AreEqual("Parole", page.Of(Html));
        Assert.AreEqual(0, page.Skipped, "the page's time did not run out");
    }

    [TestMethod]
    public void TheCallerGoingAway_IsNoLostSnippet()
    {
        using var gone = new CancellationTokenSource();
        gone.Cancel();
        var page = new SnippetPage(TimeProvider.System, gone.Token);

        Assert.ThrowsExactly<OperationCanceledException>(() => page.Of(Html));
    }

    [TestMethod]
    public async Task AnImapPageOutOfTime_StillMovesItsCursorPastEveryMail()
    {
        foreach (var server in new[] { ImapServers.GreenMail, ImapServers.Dovecot })
        {
            await using var mailbox = await ImapMailbox.OpenAsync(server);
            mailbox.Provider.Clock = new SteppingClock(AThirdOfThePage);
            var cursor = await mailbox.StartAsync();
            for (var i = 0; i < 5; i++)
                await mailbox.DeliverAsync(ImapMailbox.Mail($"Mail {i}", body: "Parole"));

            var batch = await mailbox.ReadAsync(cursor);

            Assert.AreEqual(5, batch.Changes.Count);
            CollectionAssert.AreEqual(ThreeOfFive, batch.Changes.Select(c => c.Snippet).ToArray());
            Assert.AreEqual(2, batch.SnippetsSkipped);
            Assert.AreEqual(0, (await mailbox.ReadAsync(batch.Next)).Changes.Count(c => c.Type == ChangeType.Added));
        }
    }
}
