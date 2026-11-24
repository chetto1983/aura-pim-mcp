using CalendarMcp.Core.Tools;
using System.Reflection;

namespace CalendarMcp.Tests.Tools;

internal static class EventToolSchema
{
    /// <summary>The schema the SDK publishes is the method's parameters, so a parameter is the account a caller may name.</summary>
    public static void AssertNoAccountId(Type tool, string method)
    {
        var parameters = tool.GetMethod(method)!.GetParameters().Select(p => p.Name).ToList();

        CollectionAssert.Contains(parameters, "eventId");
        CollectionAssert.DoesNotContain(parameters, "accountId");
    }
}

internal static class EventJson
{
    /// <summary>The provider's own id, recovered from the reference an event carries.</summary>
    public static string RawId(this System.Text.Json.JsonElement evt)
    {
        Assert.IsTrue(EventRef.TryDecode(evt.GetProperty("eventId").GetString(), out _, out var rawId));
        return rawId;
    }
}
