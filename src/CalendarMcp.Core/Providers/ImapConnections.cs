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
/// <see cref="LogInAsync(ImapClient, ImapAccountConfig, CancellationToken)"/>. <c>Open*</c> is both.
/// </summary>
internal static class ImapConnections
{
    public static async Task<ImapClient> OpenImapAsync(ImapAccountConfig cfg, Socket? socket, CancellationToken ct)
    {
        var client = await ConnectImapAsync(cfg, socket, ct);
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

    public static async Task<SmtpClient> OpenSmtpAsync(ImapAccountConfig cfg, Socket? socket, CancellationToken ct)
    {
        var client = await ConnectSmtpAsync(cfg, socket, ct);
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
    public static Task<ImapClient> ConnectImapAsync(ImapAccountConfig cfg, Socket? socket, CancellationToken ct) =>
        ConnectAsync(ImapNoCodes.NewClient(), cfg.ImapHost, cfg.ImapPort, cfg.ImapSecurity, socket, ct);

    public static Task<SmtpClient> ConnectSmtpAsync(ImapAccountConfig cfg, Socket? socket, CancellationToken ct) =>
        ConnectAsync(new SmtpClient(), cfg.SmtpHost, cfg.SmtpPort, cfg.SmtpSecurity, socket, ct);

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
        T client, string host, int port, string? security, Socket? socket, CancellationToken ct)
        where T : IMailService
    {
        try
        {
            var options = Options(security);
            if (socket is null)
                await client.ConnectAsync(host, port, options, ct);
            else
                await client.ConnectAsync(socket, host, port, options, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            socket?.Dispose();
            throw;
        }
    }
}
