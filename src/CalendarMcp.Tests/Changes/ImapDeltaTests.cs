using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Changes.Imap;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class ImapDeltaTests
{
    [TestMethod]
    [DataRow(new uint[] { }, "")]
    [DataRow(new uint[] { 7 }, "7")]
    [DataRow(new uint[] { 1, 2, 3 }, "1:3")]
    [DataRow(new uint[] { 1, 2, 3, 7, 9, 10 }, "1:3,7,9:10")]
    [DataRow(new uint[] { 3, 1, 2, 2 }, "1:3")]
    public void Compact_WritesRanges(uint[] uids, string expected)
    {
        Assert.AreEqual(expected, ImapDelta.Compact(uids));
    }

    [TestMethod]
    [DataRow("", new uint[] { })]
    [DataRow("7", new uint[] { 7 })]
    [DataRow("1:3,7,9:10", new uint[] { 1, 2, 3, 7, 9, 10 })]
    public void Expand_ReadsThemBack(string compact, uint[] expected)
    {
        CollectionAssert.AreEqual(expected, ImapDelta.Expand(compact).ToArray());
    }

    [TestMethod]
    [DataRow("1:4294967295")]
    [DataRow("1:1001")]
    [DataRow("1:600,1000:1500")]
    [DataRow("not a uid")]
    [DataRow("1:2:3")]
    [DataRow("5:1")]
    [DataRow("-1")]
    [DataRow("1,,2")]
    public void AForgedSet_IsRefusedBeforeItIsExpanded(string compact)
    {
        var failure = Assert.ThrowsExactly<ChangesException>(() => ImapDelta.Expand(compact));

        Assert.AreEqual("invalid_cursor", failure.Error);
    }

    [TestMethod]
    public void AFullWindow_FitsThroughExpand()
    {
        Assert.AreEqual(ImapDelta.KnownWindow, ImapDelta.Expand($"1:{ImapDelta.KnownWindow}").Count);
    }

    [TestMethod]
    public void TheHighestUid_DoesNotOverflow()
    {
        CollectionAssert.AreEqual(new[] { 4294967294u, 4294967295u }, ImapDelta.Expand("4294967294:4294967295").ToArray());
    }

    [TestMethod]
    public void Vanished_IsWhatTheServerNoLongerHas()
    {
        CollectionAssert.AreEqual(new uint[] { 2, 4 }, ImapDelta.Vanished([1, 2, 3, 4], [1, 3, 9]).ToArray());
    }

    [TestMethod]
    public void NewSince_KeepsOnlyWhatIsPastThePosition()
    {
        CollectionAssert.AreEqual(new uint[] { 11, 12 }, ImapDelta.NewSince(10, [12, 11]).ToArray());
    }

    [TestMethod]
    public void NewSince_IgnoresTheLastMailAnEmptyStarRangeAlwaysMatches()
    {
        // "11:*" on a mailbox whose newest UID is 10 answers 10 (RFC 9051 section 6.4.8).
        Assert.AreEqual(0, ImapDelta.NewSince(10, [10]).Count);
    }

    [TestMethod]
    public void KnownAfter_KeepsTheNewest()
    {
        var known = Enumerable.Range(1, ImapDelta.KnownWindow).Select(i => (uint)i);

        var after = ImapDelta.KnownAfter(known, [5], [2000, 2001]);

        Assert.AreEqual(ImapDelta.KnownWindow, after.Count);
        Assert.AreEqual(2001u, after[^1]);
        Assert.AreEqual(2u, after[0], "one arrival takes the deleted mail's place and the other pushes out the oldest");
        Assert.IsFalse(after.Contains(5u));
    }

    [TestMethod]
    public void AllUpdatesThatFit_AdvanceToTheFoldersHighest()
    {
        var cut = ImapDelta.CutUpdates([(1, 11), (2, 12)], 5, 99);

        CollectionAssert.AreEqual(new uint[] { 1, 2 }, cut.Uids.ToArray());
        Assert.AreEqual(99UL, cut.NextModSeq);
        Assert.IsFalse(cut.HasMore);
    }

    [TestMethod]
    public void ACutPage_StandsAtItsLastModSeq()
    {
        var cut = ImapDelta.CutUpdates([(3, 13), (1, 11), (2, 12)], 2, 99);

        CollectionAssert.AreEqual(new uint[] { 1, 2 }, cut.Uids.ToArray());
        Assert.AreEqual(12UL, cut.NextModSeq);
        Assert.IsTrue(cut.HasMore);
    }

    [TestMethod]
    public void ACutNeverSplitsMailSharingAModSeq()
    {
        // A cursor stands between two values, so stopping inside a group would lose the rest of it.
        var cut = ImapDelta.CutUpdates([(1, 11), (2, 12), (3, 12), (4, 12), (5, 13)], 2, 99);

        CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4 }, cut.Uids.ToArray());
        Assert.AreEqual(12UL, cut.NextModSeq);
        Assert.IsTrue(cut.HasMore);
    }

    [TestMethod]
    public void AGroupThatEndsTheList_LeavesNothingForLater()
    {
        var cut = ImapDelta.CutUpdates([(1, 12), (2, 12), (3, 12)], 1, 99);

        Assert.AreEqual(3, cut.Uids.Count);
        Assert.IsFalse(cut.HasMore);
        Assert.AreEqual(12UL, cut.NextModSeq);
    }
}
