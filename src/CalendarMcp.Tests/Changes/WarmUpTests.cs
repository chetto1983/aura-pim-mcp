using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Utilities;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// The warm-up is work no mail asked for, so its failure must cost the mail at hand its snippet and nothing
/// more: not the page, and not every page after it.
/// </summary>
[TestClass]
public class WarmUpTests
{
    private Lazy<string> _usual = null!;

    [TestInitialize]
    public void FailTheFirstWarmUp()
    {
        _usual = MailBody.WarmedUp;
        var calls = 0;
        MailBody.WarmedUp = MailBody.Warmer(() => ++calls == 1 ? throw new InvalidOperationException("no room to warm up") : "warm");
    }

    [TestCleanup]
    public void Restore() => MailBody.WarmedUp = _usual;

    [TestMethod]
    public void AFailedWarmUp_LeavesThatSnippetEmptyAndTheNextOneRead()
    {
        Assert.AreEqual(string.Empty, MailSnippet.Of("<p>Parole</p>", MailBody.HtmlFormat));
        Assert.AreEqual("Parole", MailSnippet.Of("<p>Parole</p>", MailBody.HtmlFormat));
    }

    [TestMethod]
    public void AFailedWarmUp_LeavesThatSnippetOfThePageEmptyAndTheNextOneRead()
    {
        var page = new SnippetPage(TimeProvider.System, default);
        (string, string) Html() => ("<p>Parole</p>", MailBody.HtmlFormat);

        Assert.AreEqual(string.Empty, page.Of(Html));
        Assert.AreEqual("Parole", page.Of(Html));
    }
}
