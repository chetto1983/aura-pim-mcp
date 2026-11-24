using CalendarMcp.Core.Apps;

namespace CalendarMcp.Tests.Apps;

/// <summary>The view fetches a detail by calling the tool that returns it, by that tool's own name.</summary>
[TestClass]
public sealed class CalendarViewTests
{
    [TestMethod]
    [DataRow("get_email_details")]
    [DataRow("get_calendar_event_details")]
    public void TheView_CallsTheDetailToolByName(string tool) =>
        StringAssert.Contains(CalendarView.Html, $"tool: \"{tool}\"");

    [TestMethod]
    public void TheView_NamesNoActionAndNoCuratedTool()
    {
        Assert.DoesNotContain("action:", CalendarView.Html);
        Assert.DoesNotContain("callTool(\"calendar\"", CalendarView.Html);
    }

    [TestMethod]
    public void TheView_InlinesEveryPart()
    {
        Assert.DoesNotContain("/*{{", CalendarView.Html);
        StringAssert.Contains(CalendarView.Html, "function detailRequest(it)");
        StringAssert.Contains(CalendarView.Html, "function rowNode(it)");
    }
}
