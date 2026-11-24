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
public class DeleteEventToolTests
{
    private static readonly string Reference = EventRef.Encode("acc-1", "ev-1");

    [TestMethod]
    public async Task DeleteEvent_ForwardsTheDecodedEventAndEchoesTheReference()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.DeleteEventAsync("acc-1", "primary", "ev-1", Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new DeleteEventTool(regExp.Instance(), factExp.Instance(),
            NullLogger<DeleteEventTool>.Instance);

        var result = await tool.DeleteEvent(Reference);
        var doc = JsonDocument.Parse(result);

        Assert.IsTrue(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual(Reference, doc.RootElement.GetProperty("eventId").GetString());
        Assert.DoesNotContain("ev-1", result);

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task DeleteEvent_AccountNotFound_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("nonexistent")
            .ReturnValue(Task.FromResult<AccountInfo?>(null));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new DeleteEventTool(regExp.Instance(), factExp.Instance(),
            NullLogger<DeleteEventTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.DeleteEvent(EventRef.Encode("nonexistent", "ev-1")));
        Assert.AreEqual("Account 'nonexistent' not found", ex.Message);
        regExp.Verify();
    }

    [TestMethod]
    public async Task DeleteEvent_RejectsAProviderIdPassedAsReference()
    {
        var tool = new DeleteEventTool(new IAccountRegistryCreateExpectations().Instance(),
            new IProviderServiceFactoryCreateExpectations().Instance(), NullLogger<DeleteEventTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(() => tool.DeleteEvent("ev-1"));

        StringAssert.StartsWith(ex.Message, "eventId is not a valid event reference.");
    }

    [TestMethod]
    public Task DeleteEvent_HasNoAccountIdParameter() =>
        EventToolSchema.AssertNoAccountId("delete_event");
}
