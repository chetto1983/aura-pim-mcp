using CalendarMcp.HttpServer.Admin;

namespace CalendarMcp.Tests.Admin;

[TestClass]
public sealed class TenantProviderConfigTests
{
    [TestMethod]
    [DataRow("source", "local", "filePath", "/app/data/tenants/other/google-token.json")]
    [DataRow("source", "LOCAL", "filePath", "/etc/passwd")]
    [DataRow("Source", "onedrive", "filePath", "/etc/passwd")]
    [DataRow("source", "onedrive", "emailsFilePath", "/etc/passwd")]
    [DataRow("source", "onedrive", "CONTACTSFILEPATH", "/etc/passwd")]
    public void JsonAccount_RejectsEveryWayToNameAServerFile(string sourceKey, string source, string pathKey, string path)
    {
        var config = new Dictionary<string, string>
        {
            [sourceKey] = source,
            ["oneDrivePath"] = "/Calendars/work.json",
            [pathKey] = path,
        };

        var (valid, error) = TenantProviderConfig.Validate("json", config);

        Assert.IsFalse(valid);
        StringAssert.Contains(error, "must use source 'onedrive'");
    }

    [TestMethod]
    public void JsonAccount_OnOneDriveIsAccepted()
    {
        var config = new Dictionary<string, string>
        {
            ["source"] = "onedrive",
            ["oneDrivePath"] = "/Calendars/work.json",
        };

        Assert.AreEqual((true, (string?)null), TenantProviderConfig.Validate("json", config));
    }

    [TestMethod]
    public void OtherProviders_KeepUpstreamRules()
    {
        var imap = new Dictionary<string, string>
        {
            ["imapHost"] = "imap.example.com",
            ["smtpHost"] = "smtp.example.com",
            ["username"] = "me@example.com",
            ["password"] = "app-password",
        };

        Assert.AreEqual((true, (string?)null), TenantProviderConfig.Validate("imap", imap));
        Assert.IsFalse(TenantProviderConfig.Validate("imap", []).IsValid);
    }
}
