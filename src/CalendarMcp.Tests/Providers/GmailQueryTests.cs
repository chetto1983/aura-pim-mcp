using CalendarMcp.Core.Providers;

namespace CalendarMcp.Tests.Providers;

[TestClass]
public class GmailQueryTests
{
    [TestMethod]
    public void ExactInstants_BecomeEpochSeconds()
    {
        var from = new DateTime(2026, 10, 31, 6, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 31, 18, 30, 15, DateTimeKind.Utc);

        var query = GmailQuery.WithDateBounds("invoice", from, to);

        Assert.AreEqual("invoice after:1793426400 before:1793471415", query);
    }

    [TestMethod]
    public void MailAfterMiddayStaysInsideAnEveningBound()
    {
        // The reported bug: "yyyy/MM/dd" is midnight Pacific, so a day's bound stopped at 09:00
        // in Italy. Epoch seconds keep the instant the caller asked for.
        var to = new DateTime(2026, 10, 31, 22, 59, 59, DateTimeKind.Utc);

        var query = GmailQuery.WithDateBounds("", null, to);

        Assert.AreEqual($"before:{new DateTimeOffset(to).ToUnixTimeSeconds()}", query);
        Assert.IsFalse(query.Contains('/'), "a date literal would be read as midnight Pacific");
    }

    [TestMethod]
    public void DateOnlyUpperBound_IncludesThatWholeDay()
    {
        // The tool says "on or before this date", and a date arrives as midnight.
        var to = new DateTime(2026, 10, 31);

        var query = GmailQuery.WithDateBounds("", null, to);

        var nextMidnight = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.AreEqual($"before:{nextMidnight}", query);
    }

    [TestMethod]
    public void LocalKind_IsConvertedToTheSameInstant()
    {
        var utc = new DateTime(2026, 10, 31, 6, 0, 0, DateTimeKind.Utc);

        var query = GmailQuery.WithDateBounds("", utc.ToLocalTime(), null);

        Assert.AreEqual($"after:{new DateTimeOffset(utc).ToUnixTimeSeconds()}", query);
    }

    [TestMethod]
    public void NoBounds_LeaveTheQueryAlone()
    {
        Assert.AreEqual("from:a@example.com", GmailQuery.WithDateBounds("from:a@example.com", null, null));
    }

    [TestMethod]
    public void EmptyQuery_HasNoLeadingSpace()
    {
        var from = new DateTime(2026, 10, 31, 6, 0, 0, DateTimeKind.Utc);

        Assert.AreEqual("after:1793426400", GmailQuery.WithDateBounds("  ", from, null));
    }
}
