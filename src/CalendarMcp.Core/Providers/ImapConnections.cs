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
/// <see cref="LogInAsync(ImapClient, ImapAccountConfig, CancellationToken)"/>. <c>Open*</c> is both. Every connection
/// is a socket <see cref="HostGuard"/> opened with the config's <see cref="ImapAccountConfig.AllowPrivateHosts"/>, so
/// there is no path that reaches a host without its check.
/// </summary>
internal static class ImapConnections
{
    public static async Task<ImapClient> OpenImapAsync(HostGuard guard, ImapAccountConfig cfg, CancellationToken ct)
    {
        var client = await ConnectImapAsync(guard, cfg, ct);
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
        var client = await ConnectSmtpAsync(guard, cfg, ct);
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
    /// <exception cref="PrivateAddressException">The host resolves to a private address the config may not reach.</exception>
    public static Task<ImapClient> ConnectImapAsync(HostGuard guard, ImapAccountConfig cfg, CancellationToken ct) =>
        ConnectAsync(ImapNoCodes.NewClient(), guard, cfg.ImapHost, cfg.ImapPort, cfg.ImapSecurity, cfg.AllowPrivateHosts, ct);

    /// <exception cref="PrivateAddressException">The host resolves to a private address the config may not reach.</exception>
    public static Task<SmtpClient> ConnectSmtpAsync(HostGuard guard, ImapAccountConfig cfg, CancellationToken ct) =>
        ConnectAsync(new SmtpClient(), guard, cfg.SmtpHost, cfg.SmtpPort, cfg.SmtpSecurity, cfg.AllowPrivateHosts, ct);

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
    // MailKit's own connect bounds resolving and connecting by the client's Timeout and answers a TimeoutException
    // when that runs out (MailKit 4.18.1 SocketUtils.cs#L236-249); a pre-connected socket skips that, so it is
    // restored here around the guard.
    internal static async Task<T> ConnectAsync<T>(
        T client, HostGuard guard, string host, int port, string? security, bool allowPrivate, CancellationToken ct)
        where T : IMailService
    {
        try
        {
            var socket = await GuardedSocketAsync(guard, host, port, allowPrivate, client.Timeout, ct);
            try
            {
                await client.ConnectAsync(socket, host, port, Options(security), ct);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<Socket> GuardedSocketAsync(
        HostGuard guard, string host, int port, bool allowPrivate, int timeoutMs, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(timeoutMs);
        try
        {
            return await guard.ConnectAsync(host, port, allowPrivate, bound.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Connecting to '{host}' took longer than {timeoutMs} ms.");
        }
    }
}
