using CalendarMcp.Core.Discovery;

namespace CalendarMcp.Tests.Discovery;

/// <summary>
/// What an autoconfig answer is worth: the first IMAP and the first SMTP server with a DNS hostname, a port, TLS and a
/// password login. Fixtures are ISPDB answers recorded 2026-11-20 with their hosts rewritten to example.org.
/// </summary>
[TestClass]
public class AutoconfigParserTests
{
    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Autoconfig", name));

    [TestMethod]
    public void Ispdb_TakesTheFirstUsableServer()
    {
        var found = AutoconfigParser.Parse(Fixture("yahoo-two-imap.xml"), "person", "example.org");

        Assert.AreEqual(
            new DiscoveryResult("imap", null, "imap.example.org", 993, "ssl", "smtp.example.org", 465, "ssl", "person@example.org"),
            found);
    }

    [TestMethod]
    public void Ispdb_OAuthOnlyIsAMiss()
    {
        Assert.IsNull(AutoconfigParser.Parse(Fixture("outlook-oauth-only.xml"), "person", "example.org"));
    }

    [TestMethod]
    public void Ispdb_PlainIsAMiss()
    {
        Assert.IsNull(AutoconfigParser.Parse(Fixture("plain.xml"), "person", "example.org"));
    }

    [TestMethod]
    public void Ispdb_SubstitutesTheDomainInTheHostname()
    {
        var found = AutoconfigParser.Parse(Fixture("emaildomain-hostname.xml"), "person", "example.net");

        Assert.IsNotNull(found);
        Assert.AreEqual("mail.example.net", found.ImapHost);
        Assert.AreEqual("mail.example.net", found.SmtpHost);
        Assert.AreEqual("person@example.net", found.Username);
    }

    [TestMethod]
    [DataRow("<port>0</port>")]
    [DataRow("<port>65536</port>")]
    [DataRow("<port>imap</port>")]
    [DataRow("")]
    public void Ispdb_IgnoresAServerWithoutAValidPort(string port)
    {
        var found = AutoconfigParser.Parse(FirstImapServer("<port>993</port>", port), "person", "example.org");

        Assert.AreEqual("export.imap.example.org", found?.ImapHost, "the next IMAP server is taken, not a miss");
    }

    [TestMethod]
    [DataRow("imap example.org")]
    [DataRow("%EMAILADDRESS%")]
    [DataRow("imap.example.org/path")]
    [DataRow("")]
    [DataRow("localhost")]
    [DataRow("%EMAILLOCALPART%")]
    public void Ispdb_IgnoresAServerWithAnInvalidHostname(string hostname)
    {
        var found = AutoconfigParser.Parse(
            FirstImapServer("<hostname>imap.example.org</hostname>", $"<hostname>{hostname}</hostname>"), "person", "example.org");

        Assert.AreEqual("export.imap.example.org", found?.ImapHost, "the next IMAP server is taken, not a miss");
    }

    [TestMethod]
    public void Ispdb_AFullyQualifiedHostname_LosesItsFinalDot()
    {
        var found = AutoconfigParser.Parse(
            FirstImapServer("<hostname>imap.example.org</hostname>", "<hostname>IMAP.example.org.</hostname>"), "person", "example.org");

        Assert.AreEqual("imap.example.org", found?.ImapHost, "the same server, named canonically, not the next one");
    }

    [TestMethod]
    public void Ispdb_StartTls587_KeepsStarttls()
    {
        var found = AutoconfigParser.Parse(Fixture("starttls-587.xml"), "person", "example.org");

        Assert.AreEqual(
            new DiscoveryResult("imap", null, "imap.example.org", 143, "starttls", "smtp.example.org", 587, "starttls", "person@example.org"),
            found);
    }

    [TestMethod]
    public void Ispdb_ADocumentTypeDeclaration_IsRefused()
    {
        var xml = "<!DOCTYPE clientConfig [<!ENTITY host \"imap.example.org\">]>" +
                  Fixture("starttls-587.xml").Replace("<hostname>imap.example.org</hostname>", "<hostname>&host;</hostname>");

        Assert.IsNull(AutoconfigParser.Parse(xml, "person", "example.org"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("<html><body>Not Found</body></html>")]
    [DataRow("<clientConfig><emailProvider>")]
    public void Ispdb_AnythingButAConfig_IsAMiss(string body)
    {
        Assert.IsNull(AutoconfigParser.Parse(body, "person", "example.org"));
    }

    /// <summary>The two-server Yahoo shape with one value of its first IMAP server replaced.</summary>
    private static string FirstImapServer(string value, string replacement)
    {
        var xml = Fixture("yahoo-two-imap.xml");
        var at = xml.IndexOf(value, StringComparison.Ordinal);
        Assert.IsTrue(at >= 0, $"the fixture holds {value}");
        return xml[..at] + replacement + xml[(at + value.Length)..];
    }
}
