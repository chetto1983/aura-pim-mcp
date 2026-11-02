using System.Text.Json;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tools;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Rocks;

namespace CalendarMcp.Tests.Tools;

[TestClass]
public class GetContextualEmailSummaryToolTests
{
    [TestMethod]
    public async Task GetContextualEmailSummary_NoAccounts_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAllAccountsAsync()
            .ReturnValue(Task.FromResult<IEnumerable<AccountInfo>>([]));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new GetContextualEmailSummaryTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetContextualEmailSummaryTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetContextualEmailSummary());
        Assert.AreEqual("No accounts configured", ex.Message);
        regExp.Verify();
    }

    [TestMethod]
    public async Task GetContextualEmailSummary_NoEmails_ReturnsMessage()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAllAccountsAsync()
            .ReturnValue(Task.FromResult<IEnumerable<AccountInfo>>([account]));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetEmailsAsync("acc-1", Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<IEnumerable<EmailMessage>>([]));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new GetContextualEmailSummaryTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetContextualEmailSummaryTool>.Instance);

        var result = await tool.GetContextualEmailSummary();
        var doc = JsonDocument.Parse(result);

        Assert.IsTrue(doc.RootElement.GetProperty("message").GetString()!.Contains("No emails found"));

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task GetContextualEmailSummary_WithEmails_ReturnsSummary()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365", domains: ["work.com"]);
        var emails = new List<EmailMessage>
        {
            new()
            {
                Id = "e1", AccountId = "acc-1", Subject = "Meeting tomorrow",
                From = "boss@work.com", ReceivedDateTime = DateTime.UtcNow
            },
            new()
            {
                Id = "e2", AccountId = "acc-1", Subject = "Project update",
                From = "team@work.com", ReceivedDateTime = DateTime.UtcNow, IsRead = true
            }
        };

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAllAccountsAsync()
            .ReturnValue(Task.FromResult<IEnumerable<AccountInfo>>([account]));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetEmailsAsync("acc-1", Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<IEnumerable<EmailMessage>>(emails));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new GetContextualEmailSummaryTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetContextualEmailSummaryTool>.Instance);

        var result = await tool.GetContextualEmailSummary();
        var doc = JsonDocument.Parse(result);

        Assert.AreEqual(2, doc.RootElement.GetProperty("TotalEmails").GetInt32());
        Assert.AreEqual(1, doc.RootElement.GetProperty("AccountsSearched").GetInt32());
        Assert.IsTrue(doc.RootElement.TryGetProperty("TopicClusters", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("PersonaContexts", out _));

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    private static async Task<string> PreviewOfAsync(string body, string format)
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");
        var email = new EmailMessage
        {
            Id = "e1", AccountId = "acc-1", Subject = "Una mail", From = "x@example.com",
            ReceivedDateTime = DateTime.UtcNow, Body = body, BodyFormat = format
        };

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAllAccountsAsync().ReturnValue(Task.FromResult<IEnumerable<AccountInfo>>([account]));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetEmailsAsync("acc-1", Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<IEnumerable<EmailMessage>>([email]));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new GetContextualEmailSummaryTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetContextualEmailSummaryTool>.Instance);

        var summary = JsonDocument.Parse(await tool.GetContextualEmailSummary(includeBodyPreview: true));
        return summary.RootElement.GetProperty("TopicClusters")[0].GetProperty("SampleEmails")[0].GetProperty("BodyPreview").GetString()!;
    }

    /// <summary>Where the mail itself is at hand, the preview is its converted text like any other reading of it.</summary>
    [TestMethod]
    public async Task APreviewOfAConvertedBody_HasNoHiddenText()
    {
        var preview = await PreviewOfAsync("""<p>Visible words</p><div style="display:none">SECRET</div>""", "html");

        Assert.AreEqual("Visible words", preview);
    }

    [TestMethod]
    public async Task APreviewOfABodyThatCannotBeConverted_IsEmpty()
    {
        var preview = await PreviewOfAsync($"<p>{new string('x', 2 * 1024 * 1024)}</p>", "html");

        Assert.AreEqual(string.Empty, preview);
    }

    /// <summary>A provider's own preview is all there is on a list, and nothing here can tell what in it was hidden.</summary>
    [TestMethod]
    public async Task AProvidersPreview_LosesItsInvisibleCharacters()
    {
        var preview = await PreviewOfAsync("Ci vediamo​⁠ qui\U000E0041", "text");

        Assert.AreEqual("Ci vediamo qui", preview);
    }

    [TestMethod]
    public async Task AProvidersPreview_IsCappedAfterItsInvisibleCharactersGo()
    {
        var preview = await PreviewOfAsync(new string('​', 1000) + new string('a', 149) + "\U0001F600" + "fine", "text");

        Assert.IsTrue(preview.StartsWith(new string('a', 149), StringComparison.Ordinal), preview);
        Assert.IsTrue(preview.Length <= 154, preview);
        Assert.IsFalse(char.IsSurrogate(preview.TrimEnd('.')[^1]), "a cut must not split a pair");
    }

    /// <summary>A preview is short, but a body held as text need not be, and <c>&lt;[^&gt;]+&gt;</c> is quadratic on a flood of brackets.</summary>
    [TestMethod]
    public async Task ALongBodyHeldAsText_IsNotReadPastThePreview()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await PreviewOfAsync(new string('<', 1024 * 1024), "text");

        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.Elapsed}");
    }

    /// <summary>The curated tool is what a model reads, so its text has to say it as well as the class it forwards to.</summary>
    [TestMethod]
    [DataRow(typeof(GetContextualEmailSummaryTool), nameof(GetContextualEmailSummaryTool.GetContextualEmailSummary))]
    [DataRow(typeof(CalendarActionTool), "Calendar")]
    public void TheTool_SaysAPreviewIsNotFiltered(Type tool, string method)
    {
        var description = tool.GetMethods().Single(candidate => candidate.Name == method)
            .GetParameters().Single(parameter => parameter.Name == "includeBodyPreview")
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        StringAssert.Contains(description, "cannot be filtered for hidden text");
    }

    [TestMethod]
    public async Task GetContextualEmailSummary_FailedAccount_ReportedInWarnings()
    {
        var okAccount = TestData.CreateAccount(id: "acc-ok", provider: "microsoft365", domains: ["work.com"]);
        var staleAccount = TestData.CreateAccount(id: "acc-stale", provider: "google");
        var emails = new List<EmailMessage>
        {
            new()
            {
                Id = "e1", AccountId = "acc-ok", Subject = "Meeting tomorrow",
                From = "boss@work.com", ReceivedDateTime = DateTime.UtcNow
            }
        };

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAllAccountsAsync()
            .ReturnValue(Task.FromResult<IEnumerable<AccountInfo>>([okAccount, staleAccount]));

        var okProvExp = new IProviderServiceCreateExpectations();
        okProvExp.Setups.GetEmailsAsync("acc-ok", Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<IEnumerable<EmailMessage>>(emails));

        var staleProvExp = new IProviderServiceCreateExpectations();
        staleProvExp.Setups.GetEmailsAsync("acc-stale", Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromException<IEnumerable<EmailMessage>>(new AccountAuthenticationRequiredException("acc-stale")));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(okProvExp.Instance());
        factExp.Setups.GetProvider("google").ReturnValue(staleProvExp.Instance());

        var tool = new GetContextualEmailSummaryTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetContextualEmailSummaryTool>.Instance);

        var doc = JsonDocument.Parse(await tool.GetContextualEmailSummary());

        Assert.AreEqual(1, doc.RootElement.GetProperty("TotalEmails").GetInt32());
        var warnings = doc.RootElement.GetProperty("Warnings");
        Assert.AreEqual(1, warnings.GetArrayLength());
        Assert.AreEqual("acc-stale", warnings[0].GetProperty("AccountId").GetString());
        StringAssert.Contains(warnings[0].GetProperty("Error").GetString(), "requires re-authentication");
    }

    [TestMethod]
    public async Task GetContextualEmailSummary_NoEmailsBecauseAccountFailed_ReportsWarning()
    {
        // "No emails found" must not hide that the only account couldn't be read at all.
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAllAccountsAsync()
            .ReturnValue(Task.FromResult<IEnumerable<AccountInfo>>([account]));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetEmailsAsync("acc-1", Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromException<IEnumerable<EmailMessage>>(new AccountAuthenticationRequiredException("acc-1")));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new GetContextualEmailSummaryTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetContextualEmailSummaryTool>.Instance);

        var doc = JsonDocument.Parse(await tool.GetContextualEmailSummary());

        Assert.AreEqual("No emails found matching criteria", doc.RootElement.GetProperty("message").GetString());
        var warnings = doc.RootElement.GetProperty("warnings");
        Assert.AreEqual(1, warnings.GetArrayLength());
        Assert.AreEqual("acc-1", warnings[0].GetProperty("AccountId").GetString());
    }
}
