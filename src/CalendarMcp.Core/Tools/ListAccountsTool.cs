using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// MCP tool for listing all configured accounts
/// </summary>
[McpServerToolType]
public sealed class ListAccountsTool(
    IAccountRegistry accountRegistry,
    ISendingAddresses sendingAddresses,
    ILogger<ListAccountsTool> logger)
{
    [McpServerTool, Description("List all configured accounts with their capabilities and permissions. Returns accountId, provider, displayName, email (the address the account sends from, when known), domains, capabilities (calendar, email, contacts), and permissions (emailRead, emailSend, calendarRead, calendarWrite, contactsRead, contactsWrite) for each. Permissions are what the account actually allows — a false value means every tool needing it will be refused for that account, so pick an account whose permissions cover the operation. Use the accountId values when calling other tools to scope operations to a specific account.")]
    public async Task<string> ListAccounts()
    {
        logger.LogInformation("Listing all accounts");

        try
        {
            var accounts = (await accountRegistry.GetAllAccountsAsync()).ToList();
            var addresses = await Task.WhenAll(accounts.Select(a => sendingAddresses.AddressAsync(a, CancellationToken.None)));

            var response = new
            {
                accounts = accounts.Select((a, i) => new
                {
                    accountId = a.Id,
                    provider = a.Provider,
                    displayName = a.DisplayName,
                    email = addresses[i],
                    domains = a.Domains,
                    capabilities = AccountCapabilities.GetCapabilities(a)
                        .Select(c => new { name = c.Name, readOnly = c.ReadOnly }),
                    permissions = Describe(AccountCapabilities.GetEffectivePermissions(a))
                })
            };

            // An account with no known sending address names none, rather than a null.
            return JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error listing accounts");
            throw ToolGuard.Failure("list accounts", ex);
        }
    }

    /// <summary>
    /// Projects effective permissions into the camelCase shape the tool documents.
    /// </summary>
    private static object Describe(AccountPermissions permissions) => new
    {
        emailRead = permissions.EmailRead,
        emailSend = permissions.EmailSend,
        calendarRead = permissions.CalendarRead,
        calendarWrite = permissions.CalendarWrite,
        contactsRead = permissions.ContactsRead,
        contactsWrite = permissions.ContactsWrite
    };
}
