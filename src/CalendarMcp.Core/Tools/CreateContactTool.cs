using System.ComponentModel;
using System.Text.Json;
using CalendarMcp.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// MCP tool for creating contacts
/// </summary>
[McpServerToolType]
public sealed class CreateContactTool(
    IAccountRegistry accountRegistry,
    IProviderServiceFactory providerFactory,
    ILogger<CreateContactTool> logger)
{
    [McpServerTool(Name = "create_contact"), Description("Create a new contact. Required: accountId, the account to create it in (call list_accounts). When the user has more than one account and has not named one, ask which.")]
    public async Task<string> CreateContact(
        [Description("Contact display name")] string displayName,
        [Description("Required. Account ID to create the contact in, from list_accounts. The contact is created in exactly this account.")] string accountId,
        [Description("First/given name")] string? givenName = null,
        [Description("Last/family name")] string? surname = null,
        [Description("Email address (or comma-separated list)")] string? email = null,
        [Description("Phone number (or comma-separated list)")] string? phone = null,
        [Description("Job title")] string? jobTitle = null,
        [Description("Company name")] string? companyName = null,
        [Description("Notes about the contact")] string? notes = null)
    {
        ToolGuard.RequireAccountId(accountId, "create_contact", "create the contact in");
        ToolGuard.RequireNonEmpty(displayName, nameof(displayName));
        logger.LogInformation("Creating contact: displayName={DisplayName}, accountId={AccountId}",
            displayName, accountId);

        var account = await ToolGuard.RequireAccountAsync(
            accountRegistry, accountId, Models.AccountPermission.ContactsWrite);

        try
        {
            // Parse email and phone into lists
            var emailAddresses = ParseCommaSeparated(email);
            var phoneNumbers = ParseCommaSeparated(phone);

            var provider = providerFactory.GetProvider(account.Provider);
            var contactId = await provider.CreateContactAsync(
                account.Id, displayName, givenName, surname,
                emailAddresses, phoneNumbers, jobTitle, companyName, notes,
                CancellationToken.None);

            var result = new
            {
                success = true,
                contactId,
                accountUsed = account.Id
            };

            logger.LogInformation("Created contact in account {AccountId}", account.Id);

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error in create_contact tool");
            throw ToolGuard.Failure("create contact", ex);
        }
    }

    private static List<string>? ParseCommaSeparated(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return items.Count > 0 ? items : null;
    }
}
