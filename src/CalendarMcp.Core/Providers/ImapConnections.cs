using System.Net.Sockets;
using CalendarMcp.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using static CalendarMcp.Core.Providers.ImapProviderService;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// How an IMAP account's two servers are reached, for the provider and for the verification alike. A connection
/// is two stages, so a caller can tell a refused connection from a refused login: <c>Connect*</c>, then
/// <see cref="LogInAsync(ImapClient, ImapAccountConfig, CancellationToken)"/>. <c>Open*</c> is both, for the provider,
/// with the permission the account was stored with. Every connection is a socket <see cref="HostGuard"/> opened, so
/// there is no path that reaches a host without its check.
/// </summary>
internal static class ImapConnections
{
    public static async Task<ImapClient> OpenImapAsync(HostGuard guard, ImapAccountConfig cfg, CancellationToken ct)
    {
        var client = await ConnectImapAsync(guard, cfg, cfg.AllowPrivateHosts, ct);
        try
        {
            await LogInAsync(client, cfg, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<SmtpClient> OpenSmtpAsync(HostGuard guard, ImapAccountConfig cfg, CancellationToken ct)
    {
        var client = await ConnectSmtpAsync(guard, cfg, cfg.AllowPrivateHosts, ct);
        try
        {
            await LogInAsync(client, cfg, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ImapNoCodes.NewClient, not new ImapClient(): the provider reads NO [NOPERM] and NO [CANNOT] from it.
    /// <exception cref="PrivateAddressException">The host resolves to a private address and <paramref name="allowPrivate"/> is false.</exception>
    public static async Task<ImapClient> ConnectImapAsync(HostGuard guard, ImapAccountConfig cfg, bool allowPrivate, CancellationToken ct)
    {
        var socket = await guard.ConnectAsync(cfg.ImapHost, cfg.ImapPort, allowPrivate, ct);
        return await ConnectAsync(ImapNoCodes.NewClient(), socket, cfg.ImapHost, cfg.ImapPort, cfg.ImapSecurity, ct);
    }

    /// <exception cref="PrivateAddressException">The host resolves to a private address and <paramref name="allowPrivate"/> is false.</exception>
    public static async Task<SmtpClient> ConnectSmtpAsync(HostGuard guard, ImapAccountConfig cfg, bool allowPrivate, CancellationToken ct)
    {
        var socket = await guard.ConnectAsync(cfg.SmtpHost, cfg.SmtpPort, allowPrivate, ct);
        return await ConnectAsync(new SmtpClient(), socket, cfg.SmtpHost, cfg.SmtpPort, cfg.SmtpSecurity, ct);
    }

    public static Task LogInAsync(ImapClient client, ImapAccountConfig cfg, CancellationToken ct) =>
        client.AuthenticateAsync(cfg.Username, cfg.Password, ct);

    /// <summary>A server that offers no AUTH is not logged in to, and the client stays unauthenticated.</summary>
    public static async Task LogInAsync(SmtpClient client, ImapAccountConfig cfg, CancellationToken ct)
    {
        if (client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            await client.AuthenticateAsync(cfg.Username, cfg.Password, ct);
    }

    /// <summary>
    /// An account stored before <c>imapSecurity</c>/<c>smtpSecurity</c> existed has none and keeps MailKit's
    /// <c>Auto</c>, which on 143/587 is STARTTLS only when the server offers it. <c>starttls</c> requires it.
    /// <c>none</c> is for the plaintext test servers.
    /// </summary>
    internal static SecureSocketOptions Options(string? security) => security?.ToLowerInvariant() switch
    {
        null => SecureSocketOptions.Auto,
        "ssl" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        "none" => SecureSocketOptions.None,
        _ => throw new ProviderOperationException($"'{security}' is not a TLS mode: use 'ssl' or 'starttls'.")
    };

    // MailKit 4.18 IMailService.ConnectAsync(Socket, host, port, options, ct) wants a connected socket and wraps it
    // in a NetworkStream that owns it. The host still names the server for TLS: the stream overload hands it to
    // SslStream.AuthenticateAsClientAsync (MailKit 4.18.1 AsyncImapClient.cs#L751-755 and #L507-512,
    // AsyncSmtpClient.cs#L667-671 and #L422-427), so the certificate is checked against the configured name and
    // not against the address the socket was opened to.
    private static async Task<T> ConnectAsync<T>(
        T client, Socket socket, string host, int port, string? security, CancellationToken ct)
        where T : IMailService
    {
        try
        {
            await client.ConnectAsync(socket, host, port, Options(security), ct);
            return client;
        }
        catch
        {
            client.Dispose();
            socket.Dispose();
            throw;
        }
    }
}
