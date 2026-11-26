using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Configuration;

/// <summary>
/// Names the argument a call left out. A missing required argument fails binding with an
/// <see cref="ArgumentException"/>, which the SDK reports only as "An error occurred invoking
/// '&lt;tool&gt;'.", so a caller cannot tell what to add. Upstream registers this filter in the HTTP
/// server's <c>Program.cs</c>; the fork registers it in <c>WithCalendarMcpSurface()</c> so the stdio
/// server and the tests answer the same way.
/// </summary>
public static partial class MissingArgumentReport
{
    public static IMcpServerBuilder WithNamedMissingArguments(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(Filter));

    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("missing a value for the required parameter"))
            {
                var match = RequiredParameter().Match(ex.Message);
                var name = match.Success ? match.Groups[1].Value : "a required parameter";
                throw new McpException(
                    $"Required parameter '{name}' was not provided to '{context.Params?.Name}'. " +
                    (name == "accountId" ? AccountHint(context.Params?.Name) : "") +
                    "Check the tool's input schema and retry the call including all required parameters.");
            }
        };

    // Email tools get their account from the listing that returned the mail; calendar and contact
    // tools have no such listing.
    private static string AccountHint(string? tool) =>
        tool is not null && (tool.Contains("email") || tool.Contains("unsubscribe"))
            ? "Pass the id of the account to use: from list_accounts, or from the email's listing (get_emails, search_emails). "
            : "Pass the id of the account to use: from list_accounts. ";

    [GeneratedRegex(@"required parameter '([^']+)'")]
    private static partial Regex RequiredParameter();
}
