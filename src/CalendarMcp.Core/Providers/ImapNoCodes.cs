using System.Runtime.CompilerServices;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Remembers whether the server has said <c>NO [NOPERM]</c> or <c>NO [CANNOT]</c> (RFC 5530), which are the
/// refusals no waiting changes. MailKit takes the code out of the text an <see cref="ImapCommandException"/>
/// carries and has nowhere else to put it, so the only place it can be read is the line itself. Nothing is
/// kept of the traffic, and the client's credentials are never looked at: only server lines are scanned.
/// </summary>
internal sealed class ImapNoCodes : IProtocolLogger
{
    private static readonly ConditionalWeakTable<ImapClient, ImapNoCodes> Clients = new();

    private volatile bool _permanent;

    /// <summary>A client whose NO codes can be asked for with <see cref="Of"/>.</summary>
    public static ImapClient NewClient()
    {
        var codes = new ImapNoCodes();
        var client = new ImapClient(codes);
        Clients.Add(client, codes);
        return client;
    }

    public static ImapNoCodes? Of(ImapClient client) => Clients.TryGetValue(client, out var codes) ? codes : null;

    /// <summary>Whether a permanent NO has come since <see cref="Reset"/>.</summary>
    public bool SawPermanent => _permanent;

    public void Reset() => _permanent = false;

    public IAuthenticationSecretDetector AuthenticationSecretDetector { get; set; } = null!;

    public void LogServer(byte[] buffer, int offset, int count)
    {
        var span = buffer.AsSpan(offset, count);
        if (span.IndexOf(" NO [NOPERM]"u8) >= 0 || span.IndexOf(" NO [CANNOT]"u8) >= 0)
            _permanent = true;
    }

    public void LogConnect(Uri uri)
    {
    }

    public void LogClient(byte[] buffer, int offset, int count)
    {
    }

    public void Dispose()
    {
    }
}
