using CalendarMcp.Core.Changes;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class MailHeaderSignalsTests
{
    private static Func<string, string?> Headers(params (string Name, string Value)[] headers) =>
        name => headers.Where(h => h.Name == name).Select(h => h.Value).FirstOrDefault();

    [TestMethod]
    [DataRow("List-Id", "<ci.example.com>", true)]
    [DataRow("List-Unsubscribe", "<mailto:leave@example.com>", true)]
    [DataRow("Subject", "Fattura", false)]
    public void Bulk_IsReadFromListHeaders(string name, string value, bool expected)
    {
        Assert.AreEqual(expected, MailHeaderSignals.IsBulk(Headers((name, value))));
    }

    [TestMethod]
    [DataRow("In-Reply-To", "<a@example.com>", true)]
    [DataRow("References", "<a@example.com>", true)]
    [DataRow("Subject", "Re: Fattura", false)]
    public void Reply_IsReadFromThreadingHeadersNotTheSubject(string name, string value, bool expected)
    {
        Assert.AreEqual(expected, MailHeaderSignals.IsReply(Headers((name, value))));
    }

    [TestMethod]
    [DataRow("Importance", "high", true)]
    [DataRow("Importance", "High", true)]
    [DataRow("Importance", "normal", false)]
    [DataRow("Importance", "low", false)]
    [DataRow("X-Priority", "1", true)]
    [DataRow("X-Priority", "1 (Highest)", true)]
    [DataRow("X-Priority", "2", true)]
    [DataRow("X-Priority", " 2", true)]
    [DataRow("X-Priority", "3", false)]
    [DataRow("X-Priority", "5 (Lowest)", false)]
    [DataRow("Subject", "URGENT", false)]
    public void Importance_IsTheSendersClaim(string name, string value, bool expected)
    {
        Assert.AreEqual(expected, MailHeaderSignals.IsImportant(Headers((name, value))));
    }

    [TestMethod]
    [DataRow(null, false, "no header says nothing")]
    [DataRow("", false, "no header says nothing")]
    [DataRow("mx.example.com; none", false, "the server checked nothing")]
    [DataRow("mx.google.com; dkim=pass header.i=@example.com; spf=pass; dmarc=pass (p=REJECT) header.from=example.com", false, "Gmail, all pass")]
    [DataRow("mx.google.com; dkim=fail; spf=fail; dmarc=fail (p=NONE) header.from=example.com", true, "Gmail, all fail")]
    [DataRow("spf=pass (sender IP is 192.0.2.1) smtp.mailfrom=example.com; dkim=pass (signature was verified) header.d=example.com;dmarc=pass action=none header.from=example.com;compauth=pass reason=100", false, "Microsoft, all pass")]
    [DataRow("spf=fail (sender IP is 192.0.2.1) smtp.mailfrom=example.com; dkim=none; dmarc=fail action=oreject header.from=example.com;compauth=fail reason=000", true, "Microsoft, all fail")]
    [DataRow("spf=none; dkim=none; dmarc=none; compauth=fail reason=001", true, "Microsoft's composite authentication failed with no DMARC record")]
    [DataRow("mx.example.com; dmarc=bestguesspass; spf=fail", false, "Microsoft's best guess pass")]
    [DataRow("mx.example.com; dmarc=pass; spf=fail; dkim=fail", false, "DMARC weighs SPF and DKIM, and needs one aligned")]
    [DataRow("mx.example.com; spf=fail; dkim=pass", false, "a forwarded mail breaks SPF and keeps DKIM")]
    [DataRow("mx.example.com; dkim=fail; spf=pass", false, "a mailing list breaks DKIM and keeps SPF")]
    [DataRow("mx.example.com; spf=fail; dkim=none", true, "no aligned pass")]
    [DataRow("mx.example.com; dkim=fail; spf=none", true, "no aligned pass")]
    [DataRow("mx.example.com; spf=softfail; dkim=none", false, "a softfail is not a failure by itself")]
    [DataRow("mx.example.com; dmarc=pass (reported as dmarc=fail by nobody)", false, "a comment is free text")]
    [DataRow("mx.example.com; x-note=\"dmarc=fail; spf=fail\"; dkim=pass", false, "a quoted string is free text")]
    [DataRow("MX.EXAMPLE.COM; DMARC=FAIL", true, "method and result are case-insensitive")]
    public void Authentication_IsTheReceiversVerdict(string? header, bool expected, string why)
    {
        Assert.AreEqual(expected, MailHeaderSignals.AuthenticationFailed(header), why);
    }
}
