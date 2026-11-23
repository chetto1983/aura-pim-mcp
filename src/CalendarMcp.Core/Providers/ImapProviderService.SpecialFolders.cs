using MailKit;
using MailKit.Net.Imap;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// The Sent, Trash and Junk folders: the account's setting, else the folder the server marks for the role
/// (RFC 6154 SPECIAL-USE, or Gmail's XLIST), else a folder whose own name is one mail clients use for it. A
/// host without SPECIAL-USE names them freely: Aruba keeps <c>INBOX.Sent</c> and <c>INBOX.Trash</c>, and the
/// Gmail names that used to be the defaults lost every sent copy and failed every delete there (measured
/// 2026-11-23).
/// </summary>
public partial class ImapProviderService
{
    // Compared case-insensitively with a folder's last name segment, most common first.
    private static readonly Dictionary<SpecialFolder, string[]> KnownFolderNames = new()
    {
        [SpecialFolder.Sent] =
        [
            "Sent", "Sent Items", "Sent Messages", "Sent Mail",
            "Posta inviata", "Inviata", "Inviati", "Elementi inviati",
            "Gesendet", "Gesendete Elemente", "Envoyés", "Éléments envoyés", "Enviados", "Elementos enviados"
        ],
        [SpecialFolder.Trash] =
        [
            "Trash", "Deleted Items", "Deleted Messages", "Deleted",
            "Cestino", "Elementi eliminati",
            "Papierkorb", "Gelöschte Elemente", "Corbeille", "Éléments supprimés", "Papelera", "Elementos eliminados"
        ],
        [SpecialFolder.Junk] =
        [
            "Junk", "Spam", "Junk E-mail", "Junk Email", "Bulk Mail",
            "Posta indesiderata", "Indesiderata",
            "Junk-E-Mail", "Courrier indésirable", "Correo no deseado"
        ],
    };

    /// <param name="configured">The account's setting for the role, or null when it has none.</param>
    /// <returns>The folder, or null when the setting names nothing that exists and the server offers none.</returns>
    private static async Task<IMailFolder?> FindSpecialFolderAsync(
        ImapClient client, string? configured, SpecialFolder role, CancellationToken ct)
    {
        if (configured is not null)
        {
            try
            {
                return await client.GetFolderAsync(configured, ct);
            }
            catch (FolderNotFoundException)
            {
                // A setting that names nothing on this server is no reason to fail: look for the role.
            }
        }

        var special = GetSpecialFolder(client, role);
        if (special is not null || client.PersonalNamespaces.Count == 0)
            return special;

        var folders = await client.GetFoldersAsync(client.PersonalNamespaces[0], StatusItems.None, false, ct);
        var name = FolderByName(role, folders.Select(f => (f.FullName, f.Name)));
        return name is null ? null : folders.First(f => f.FullName == name);
    }

    /// <summary>The full name of the folder whose own name is a known name for the role, by that list's order.</summary>
    internal static string? FolderByName(SpecialFolder role, IEnumerable<(string FullName, string Name)> folders)
    {
        var listed = folders.ToList();
        foreach (var known in KnownFolderNames[role])
        {
            foreach (var (fullName, name) in listed)
            {
                if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase))
                    return fullName;
            }
        }

        return null;
    }
}
