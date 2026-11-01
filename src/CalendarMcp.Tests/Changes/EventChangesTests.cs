using CalendarMcp.Core.Changes;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class EventChangesTests
{
    private const string Start = "2026-11-02T09:00:00Z";
    private const string End = "2026-11-02T10:00:00Z";

    [TestMethod]
    public void Fingerprint_ChangesWhenTheEventMoves()
    {
        var before = EventChanges.Fingerprint(Start, End, EventChanges.Confirmed);

        Assert.AreNotEqual(before, EventChanges.Fingerprint("2026-11-02T10:00:00Z", End, EventChanges.Confirmed));
        Assert.AreNotEqual(before, EventChanges.Fingerprint(Start, "2026-11-02T11:00:00Z", EventChanges.Confirmed));
    }

    [TestMethod]
    public void Fingerprint_ChangesWhenTheEventIsCancelled()
    {
        Assert.AreNotEqual(
            EventChanges.Fingerprint(Start, End, EventChanges.Confirmed),
            EventChanges.Fingerprint(Start, End, EventChanges.Cancelled));
    }

    [TestMethod]
    public void Fingerprint_IsStable()
    {
        Assert.AreEqual(
            EventChanges.Fingerprint(Start, End, EventChanges.Confirmed),
            EventChanges.Facts(Start, End, EventChanges.Confirmed, true, "accepted").Fingerprint);
    }

    [TestMethod]
    public void Fingerprint_DoesNotReadWhatIsNotAMove()
    {
        var mine = EventChanges.Facts(Start, End, EventChanges.Confirmed, true, "accepted");
        var replied = EventChanges.Facts(Start, End, EventChanges.Confirmed, false, "declined");

        Assert.AreEqual(mine.Fingerprint, replied.Fingerprint);
    }

    [TestMethod]
    public void Instant_IsTheSameMomentWhateverZoneItWasWrittenIn()
    {
        var rome = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1));
        var utc = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

        Assert.AreEqual(EventChanges.Instant(utc), EventChanges.Instant(rome));
        Assert.AreEqual(Start, EventChanges.Instant(rome));
    }

    [TestMethod]
    public void Date_HasNoTime()
    {
        Assert.AreEqual("2026-11-02", EventChanges.Date(new DateOnly(2026, 11, 2)));
    }
}
