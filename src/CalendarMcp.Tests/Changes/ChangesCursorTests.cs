using System.Buffers.Text;
using System.Text;
using CalendarMcp.Core.Changes;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class ChangesCursorTests
{
    private sealed record State(string Position);

    private static string Forge(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    [TestMethod]
    public void Encoded_ReadsBackPerFolder()
    {
        var cursor = ChangesCursor.Of<State>("google.calendar",
        [
            new("work", new State("a")),
            new("home", new State("b"))
        ]);

        var read = ChangesCursor.Decode(cursor.Encode(), "google.calendar");

        Assert.AreEqual("a", read.Folder<State>("work").Position);
        Assert.AreEqual(2, read.Folders<State>().Count);
    }

    [TestMethod]
    public void Encoded_IsSafeInAUrlAndInJson()
    {
        var encoded = ChangesCursor.Of("google.mail", "INBOX", new State("???>>>~~~")).Encode();

        Assert.IsTrue(encoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), encoded);
    }

    [TestMethod]
    [DataRow("not a cursor")]
    [DataRow("e30")]
    [DataRow("bm90IGpzb24")]
    public void Garbage_IsTheCallersError(string encoded)
    {
        var failure = Assert.ThrowsExactly<ChangesException>(() => ChangesCursor.Decode(encoded, "google.mail"));

        Assert.AreEqual(400, failure.StatusCode);
        Assert.AreEqual("invalid_cursor", failure.Error);
    }

    [TestMethod]
    public void AnotherVersion_Expires()
    {
        Assert.ThrowsExactly<CursorExpiredException>(() =>
            ChangesCursor.Decode(Forge("""{"v":2,"k":"google.mail","f":{}}"""), "google.mail"));
    }

    [TestMethod]
    public void AnotherSource_IsTheCallersError()
    {
        var encoded = ChangesCursor.Of("google.mail", "INBOX", new State("a")).Encode();

        var failure = Assert.ThrowsExactly<ChangesException>(() => ChangesCursor.Decode(encoded, "imap.mail"));

        Assert.AreEqual("invalid_cursor", failure.Error);
    }

    [TestMethod]
    public void MissingFolder_IsTheCallersError()
    {
        var read = ChangesCursor.Decode(ChangesCursor.Of("google.mail", "SENT", new State("a")).Encode(), "google.mail");

        Assert.ThrowsExactly<ChangesException>(() => read.Folder<State>("INBOX"));
    }

    [TestMethod]
    public void FolderOfTheWrongShape_IsTheCallersError()
    {
        var read = ChangesCursor.Decode(Forge("""{"v":1,"k":"google.mail","f":{"INBOX":3}}"""), "google.mail");

        Assert.ThrowsExactly<ChangesException>(() => read.Folder<State>("INBOX"));
    }
}
