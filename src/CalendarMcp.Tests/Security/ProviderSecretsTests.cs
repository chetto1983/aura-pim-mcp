using CalendarMcp.Core.Security;
using Microsoft.AspNetCore.DataProtection;

namespace CalendarMcp.Tests.Security;

[TestClass]
public class ProviderSecretsTests
{
    private static PasswordProtector Protector() => new(new EphemeralDataProtectionProvider());

    [TestMethod]
    public void ProtectIncoming_EncryptsAPlainPassword()
    {
        var protector = Protector();

        var result = ProviderSecrets.ProtectIncoming(
            new Dictionary<string, string> { ["password"] = "hunter2" }, protector);

        Assert.IsTrue(PasswordProtector.IsProtected(result["password"]));
        Assert.AreEqual("hunter2", protector.Unprotect(result["password"]));
    }

    [TestMethod]
    [DataRow("password")]
    [DataRow("Password")]
    [DataRow("PASSWORD")]
    public void ProtectIncoming_EncryptsWhateverTheKeysCase(string key)
    {
        var protector = Protector();

        var result = ProviderSecrets.ProtectIncoming(
            new Dictionary<string, string> { [key] = "hunter2" }, protector);

        Assert.IsTrue(PasswordProtector.IsProtected(result[key]));
        Assert.AreEqual("hunter2", protector.Unprotect(result[key]));
    }

    [TestMethod]
    public void ProtectIncoming_EncryptsAValueThatStartsWithENC()
    {
        var protector = Protector();

        var result = ProviderSecrets.ProtectIncoming(
            new Dictionary<string, string> { ["password"] = "ENC:not-really-ciphertext" }, protector);

        Assert.AreEqual("ENC:not-really-ciphertext", protector.Unprotect(result["password"]));
        Assert.AreNotEqual("ENC:not-really-ciphertext", result["password"]);
    }

    [TestMethod]
    public void ProtectIncoming_CopiesOtherKeys()
    {
        var result = ProviderSecrets.ProtectIncoming(new Dictionary<string, string>
        {
            ["host"] = "imap.example.com",
            ["username"] = "me",
            ["password"] = "",
        }, Protector());

        Assert.AreEqual("imap.example.com", result["HOST"]);
        Assert.AreEqual("me", result["username"]);
        Assert.AreEqual("", result["password"]);
    }

    [TestMethod]
    public void IsSecretKey_IsPasswordInAnyCase()
    {
        Assert.IsTrue(ProviderSecrets.IsSecretKey("Password"));
        Assert.IsFalse(ProviderSecrets.IsSecretKey("username"));
    }
}
