using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Mail is laid out with tables. A table that holds data stays a table; one that only positions
/// its content becomes a run of blocks, so its text survives without pipes around it. A cell turns
/// into a block rather than being unwrapped, or neighbouring cells would run together.
/// </summary>
internal static class LayoutTables
{
    public static void Flatten(IElement root)
    {
        foreach (var table in root.QuerySelectorAll("table").OfType<IHtmlTableElement>().Where(IsLayout).ToList())
            FlattenTable(table);
    }

    private static bool IsLayout(IHtmlTableElement table) =>
        table.GetAttribute("role") is "presentation" or "none"
        || table.QuerySelector("table") is not null
        || table.Rows.Length < 2
        || table.Rows.All(row => row.Cells.Length < 2);

    private static void FlattenTable(IHtmlTableElement table)
    {
        foreach (var cell in table.Rows.SelectMany(row => row.Cells).ToList())
        {
            var block = table.Owner!.CreateElement("div");
            while (cell.FirstChild is { } child)
                block.AppendChild(child);
            table.Before(block);
        }

        table.Remove();
    }
}
