using CalendarMcp.Core.Changes;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class WrittenToLookupTests
{
    private int _senderQueries;
    private int _threadQueries;

    private WrittenToLookup Lookup(bool? sent, bool? inThread = false) => new(
        (_, _) =>
        {
            _senderQueries++;
            return Task.FromResult(sent);
        },
        (_, _) =>
        {
            _threadQueries++;
            return Task.FromResult(inThread);
        });

    [TestMethod]
    public async Task ABurstFromOneSender_CostsOneQuery()
    {
        var lookup = Lookup(true);

        Assert.IsTrue(await lookup.ForMailAsync("a@example.com", false, false, "t1", default));
        Assert.IsTrue(await lookup.ForMailAsync("A@EXAMPLE.COM", false, false, "t2", default));
        Assert.IsTrue(await lookup.ForMailAsync("a@example.com", false, true, "t3", default));

        Assert.AreEqual(1, _senderQueries);
        Assert.AreEqual(0, _threadQueries);
    }

    [TestMethod]
    public async Task Bulk_AsksNothing()
    {
        Assert.IsNull(await Lookup(true).ForMailAsync("a@example.com", true, true, "t1", default));

        Assert.AreEqual(0, _senderQueries + _threadQueries);
    }

    [TestMethod]
    public async Task NoSender_AsksNothing()
    {
        Assert.IsNull(await Lookup(true).ForMailAsync(null, false, true, "t1", default));

        Assert.AreEqual(0, _senderQueries);
    }

    [TestMethod]
    public async Task CannotTell_IsNull()
    {
        Assert.IsNull(await Lookup(null).ForMailAsync("a@example.com", false, true, "t1", default));

        Assert.AreEqual(0, _threadQueries);
    }

    [TestMethod]
    public async Task AReplyInAThreadTheAccountWroteIn_CountsFromAStranger()
    {
        Assert.IsTrue(await Lookup(false, inThread: true).ForMailAsync("new@example.com", false, true, "t1", default));
    }

    [TestMethod]
    public async Task ANonReply_DoesNotAskTheThread()
    {
        Assert.IsFalse(await Lookup(false, inThread: true).ForMailAsync("new@example.com", false, false, "t1", default));

        Assert.AreEqual(0, _threadQueries);
    }

    [TestMethod]
    public async Task AThread_IsAskedOnce()
    {
        var lookup = Lookup(false, inThread: false);

        await lookup.ForMailAsync("a@example.com", false, true, "t1", default);
        await lookup.ForMailAsync("b@example.com", false, true, "t1", default);

        Assert.AreEqual(1, _threadQueries);
        Assert.AreEqual(2, _senderQueries);
    }

    [TestMethod]
    public async Task AThreadThatCannotTell_IsNullNotFalse()
    {
        Assert.IsNull(await Lookup(false, inThread: null).ForMailAsync("a@example.com", false, true, "t1", default));
    }

    [TestMethod]
    public async Task AFailedQuery_FailsThePage()
    {
        var lookup = new WrittenToLookup(
            (_, _) => throw new HttpRequestException("reset"),
            (_, _) => Task.FromResult<bool?>(false));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => lookup.ForMailAsync("a@example.com", false, false, null, default));
    }
}
