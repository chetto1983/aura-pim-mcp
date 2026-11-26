using System.Reflection;
using CalendarMcp.Core.Apps;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Prompts;
using CalendarMcp.Core.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// Registers this fork's whole MCP surface: the tenant binding, one tool per operation, the MCP Apps
/// view, the <c>attachment://stash/{id}</c> resource, the quoted-scalar argument filter, the filter
/// naming a missing required argument and the three prompt classes. Both hosts and the test harness
/// call this one extension, so a <c>Program.cs</c> merge in an upstream sync cannot narrow the chain,
/// and the tests exercise the chain the hosts run.
/// </summary>
public static class CalendarMcpSurface
{
    private static readonly Type[] ToolTypes =
    [
        typeof(ListAccountsTool), typeof(GetEmailsTool), typeof(GetEmailDetailsTool), typeof(SearchEmailsTool),
        typeof(ListCalendarsTool), typeof(GetCalendarEventsTool), typeof(GetCalendarEventDetailsTool),
        typeof(GetContactsTool), typeof(SearchContactsTool), typeof(GetContactDetailsTool),
        typeof(CreateEventTool), typeof(UpdateEventTool), typeof(RespondToEventTool), typeof(SendEmailTool),
        typeof(DeleteEmailTool), typeof(MarkEmailAsReadTool), typeof(MoveEmailTool), typeof(DeleteEventTool),
        typeof(CreateContactTool), typeof(UpdateContactTool), typeof(DeleteContactTool),
        typeof(GetEmailAttachmentTool), typeof(GetContextualEmailSummaryTool), typeof(GetGuideTool),
        typeof(GetUnsubscribeInfoTool), typeof(UnsubscribeFromEmailTool), typeof(BulkDeleteEmailsTool),
        typeof(BulkMarkEmailsAsReadTool), typeof(BulkMoveEmailsTool),
    ];

    /// <summary>The reads whose results the view renders.</summary>
    private static readonly HashSet<Type> ViewToolTypes =
    [
        typeof(ListAccountsTool), typeof(GetEmailsTool), typeof(GetEmailDetailsTool), typeof(SearchEmailsTool),
        typeof(ListCalendarsTool), typeof(GetCalendarEventsTool), typeof(GetCalendarEventDetailsTool),
        typeof(GetContactsTool), typeof(SearchContactsTool), typeof(GetContactDetailsTool),
    ];

    public static IMcpServerBuilder WithCalendarMcpSurface(this IMcpServerBuilder builder)
    {
        builder.WithTenantBinding();
        foreach (var type in ToolTypes)
            AddTool(builder, type);
        return builder
            .WithCalendarView()
            .WithEmailAttachmentResource()
            .WithStringScalarArguments()
            .WithNamedMissingArguments()
            .WithPrompts<CalendarPrompts>()
            .WithPrompts<EmailPrompts>()
            .WithPrompts<ContactPrompts>();
    }

    /// <remarks>
    /// The same construction <c>WithTools&lt;T&gt;()</c> performs (one instance per call, built from the
    /// request's services), written out because <c>WithTools</c> hands the tool to DI unseen and leaves
    /// nowhere to set its <c>_meta.ui</c>.
    /// </remarks>
    private static void AddTool(IMcpServerBuilder builder, Type type)
    {
        var method = type.GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
        var showsView = ViewToolTypes.Contains(type);
        builder.Services.AddSingleton<McpServerTool>(services =>
        {
            var tool = McpServerTool.Create(
                method,
                request => ActivatorUtilities.CreateInstance(
                    request.Services ?? throw new InvalidOperationException($"{type.Name} needs the request's services."),
                    type),
                new McpServerToolCreateOptions { Services = services });
#pragma warning disable MCPEXP003 // MCP Apps (SEP-1865) is experimental; see Apps/CalendarView.cs.
            return showsView ? McpApps.SetAppUi(tool, new McpUiToolMeta { ResourceUri = CalendarView.ResourceUri }) : tool;
#pragma warning restore MCPEXP003
        });
    }
}
