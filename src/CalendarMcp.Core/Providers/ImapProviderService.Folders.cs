using CalendarMcp.Core.Services;
using MailKit;
using MailKit.Net.Imap;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Folder opening and resolution: the inbox by default, a configured or SPECIAL-USE folder for
/// an alias (<c>trash</c>, <c>spam</c>, ...), otherwise a literal folder name. Shared by
/// list/search (<c>folder</c>) and <c>move_email</c> (<c>destination</c>).
/// </summary>
public partial class ImapProviderService
{
    private static async Task<IMailFolder> OpenFolderAsync(
        ImapClient client, string folderName, FolderAccess access, CancellationToken ct)
    {
        var folder = string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(folderName, ct);

        if (folder is null)
            throw new ProviderOperationException($"IMAP folder '{folderName}' not found.");

        await folder.OpenAsync(access, ct);
        return folder;
    }

    /// <summary>
    /// Opens the folder to list or search: the configured inbox by default, otherwise an
    /// alias or folder name resolved like a <c>move_email</c> destination. Returns the name
    /// that email IDs from this folder must carry.
    /// </summary>
    private static async Task<(IMailFolder Folder, string Name)> OpenListFolderAsync(
        ImapClient client, ImapAccountConfig cfg, string? folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return (await OpenFolderAsync(client, cfg.InboxFolder, FolderAccess.ReadOnly, ct), cfg.InboxFolder);

        var resolved = await ResolveFolderAsync(client, cfg, folder, ct);
        await resolved.OpenAsync(FolderAccess.ReadOnly, ct);
        return (resolved, resolved.FullName);
    }

    /// <summary>
    /// Maps a destination alias to the folder name configured for the account, or
    /// <c>null</c> when the account has no setting for it.
    /// </summary>
    internal static string? ResolveConfiguredFolder(WellKnownMailFolder folder, ImapAccountConfig cfg) =>
        folder switch
        {
            WellKnownMailFolder.Inbox => cfg.InboxFolder,
            WellKnownMailFolder.Sent => cfg.SentFolder,
            WellKnownMailFolder.Trash => cfg.TrashFolder,
            WellKnownMailFolder.Spam => cfg.JunkFolder,
            _ => null
        };

    /// <summary>
    /// Resolves a folder alias or name (a <c>move_email</c> destination or a list/search folder).
    /// Aliases (<c>trash</c>, <c>spam</c>, ...) use the account's configured folders, then the
    /// server's SPECIAL-USE folders, then a known name (<see cref="FindSpecialFolderAsync"/>);
    /// anything else is a literal folder name.
    /// </summary>
    private static async Task<IMailFolder> ResolveFolderAsync(
        ImapClient client, ImapAccountConfig cfg, string destination, CancellationToken ct)
    {
        if (!MailFolderAliases.TryParse(destination, out var wellKnown))
            return await client.GetFolderAsync(destination, ct);

        SpecialFolder? role = wellKnown switch
        {
            WellKnownMailFolder.Sent => SpecialFolder.Sent,
            WellKnownMailFolder.Trash => SpecialFolder.Trash,
            WellKnownMailFolder.Spam => SpecialFolder.Junk,
            _ => null
        };
        if (role is { } kind)
            return await FindSpecialFolderAsync(client, ResolveConfiguredFolder(wellKnown, cfg), kind, ct)
                ?? throw new ProviderOperationException(
                    $"Folder '{destination}' could not be resolved on IMAP account '{cfg.AccountId}': the server marks " +
                    "no such folder and none has a known name. Pass the literal folder name, or set it in the account's providerConfig.");

        if (wellKnown == WellKnownMailFolder.Inbox)
            return string.Equals(cfg.InboxFolder, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox ?? throw new ProviderOperationException($"IMAP account '{cfg.AccountId}' has no INBOX folder.")
                : await client.GetFolderAsync(cfg.InboxFolder, ct);

        var special = wellKnown switch
        {
            WellKnownMailFolder.Archive =>
                GetSpecialFolder(client, SpecialFolder.Archive) ?? GetSpecialFolder(client, SpecialFolder.All),
            WellKnownMailFolder.Drafts => GetSpecialFolder(client, SpecialFolder.Drafts),
            _ => null
        };
        if (special is not null)
            return special;

        // No SPECIAL-USE folder: fall back to a folder literally named after the alias.
        try
        {
            return await client.GetFolderAsync(destination.Trim(), ct);
        }
        catch (FolderNotFoundException ex)
        {
            throw new ProviderOperationException(
                $"Folder '{destination}' could not be resolved on IMAP account '{cfg.AccountId}': the server " +
                $"advertises no matching SPECIAL-USE folder and has no folder named '{destination.Trim()}'. " +
                "Pass the literal folder name instead.", ex);
        }
    }

    private static IMailFolder? GetSpecialFolder(ImapClient client, SpecialFolder folder)
    {
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) == 0)
            return null;
        return client.GetFolder(folder);
    }
}
