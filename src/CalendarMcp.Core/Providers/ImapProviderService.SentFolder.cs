using MailKit;
using MailKit.Net.Imap;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// The Sent folder: the account's <c>sentFolder</c> setting, else the folder the server marks <c>\Sent</c>
/// (RFC 6154 SPECIAL-USE, or Gmail's XLIST), else a folder whose own name is one mail clients use. A host
/// without SPECIAL-USE names it freely: Aruba keeps it at <c>INBOX.Sent</c>, and the Gmail name that used to
/// be the default lost every copy there (measured 2026-11-23).
/// </summary>
public partial class ImapProviderService
{
    // Compared case-insensitively with a folder's last name segment, most common first.
    private static readonly string[] SentFolderNames =
    [
        "Sent", "Sent Items", "Sent Messages", "Sent Mail",
        "Posta inviata", "Inviata", "Inviati", "Elementi inviati",
        "Gesendet", "Gesendete Elemente", "Envoyés", "Éléments envoyés", "Enviados", "Elementos enviados"
    ];

    /// <returns>The Sent folder, or null when the account names none that exists and the server offers none.</returns>
    private static async Task<IMailFolder?> FindSentFolderAsync(ImapClient client, ImapAccountConfig cfg, CancellationToken ct)
    {
        if (cfg.SentFolder is not null)
        {
            try
            {
                return await client.GetFolderAsync(cfg.SentFolder, ct);
            }
            catch (FolderNotFoundException)
            {
                // A setting that names nothing on this server is no reason to lose the copy.
            }
        }

        var special = GetSpecialFolder(client, SpecialFolder.Sent);
        if (special is not null || client.PersonalNamespaces.Count == 0)
            return special;

        var folders = await client.GetFoldersAsync(client.PersonalNamespaces[0], StatusItems.None, false, ct);
        var name = SentFolderByName(folders.Select(f => (f.FullName, f.Name)));
        return name is null ? null : folders.First(f => f.FullName == name);
    }

    /// <summary>The full name of the folder whose own name is a known Sent name, by the order of that list.</summary>
    internal static string? SentFolderByName(IEnumerable<(string FullName, string Name)> folders)
    {
        var listed = folders.ToList();
        foreach (var known in SentFolderNames)
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
