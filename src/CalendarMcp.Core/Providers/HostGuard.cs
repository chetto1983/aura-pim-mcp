using System.Net;
using System.Net.Sockets;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Connects to a host a tenant named, after checking where it points. The host is resolved once and every
/// address is checked; the socket goes to an address that was checked, so DNS cannot answer differently between
/// the check and the connection. Without <c>allowPrivate</c>, a host with any private address is refused:
/// loopback, RFC 1918, link-local, CGNAT 100.64/10, ULA fc00::/7, the unspecified addresses (which reach the
/// box itself on Linux), and IPv4-mapped forms of all of them.
/// </summary>
public sealed class HostGuard
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, Task<Socket>> _connect;

    public HostGuard() : this(Dns.GetHostAddressesAsync, ConnectSocketAsync)
    {
    }

    internal HostGuard(
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, Task<Socket>> connect)
    {
        _resolve = resolve;
        _connect = connect;
    }

    /// <summary>A connected socket to the first of the host's addresses that answers.</summary>
    /// <exception cref="PrivateAddressException">An address is private and <paramref name="allowPrivate"/> is false.</exception>
    public async Task<Socket> ConnectAsync(string host, int port, bool allowPrivate, CancellationToken ct)
    {
        var addresses = (await _resolve(host, ct)).Select(Unmapped).Distinct().ToList();
        if (addresses.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        if (!allowPrivate && addresses.FirstOrDefault(IsPrivate) is { } refused)
            throw new PrivateAddressException(host, refused);

        for (var i = 0; ; i++)
        {
            try
            {
                return await _connect(new IPEndPoint(addresses[i], port), ct);
            }
            catch (SocketException) when (i < addresses.Count - 1)
            {
            }
        }
    }

    private static IPAddress Unmapped(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is 0 or 10 or 127
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64);
        }

        return IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || (bytes[0] & 0xFE) == 0xFC;
    }

    private static async Task<Socket> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>A host that resolves to an address this caller may not reach.</summary>
public sealed class PrivateAddressException(string host, IPAddress address)
    : IOException($"'{host}' resolves to {address}, a private address.");
