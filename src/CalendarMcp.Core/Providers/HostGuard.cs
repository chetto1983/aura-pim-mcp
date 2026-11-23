using System.Net;
using System.Net.Sockets;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// Connects to a host a tenant named, after checking where it points. The host is resolved once and every
/// address is checked; the socket goes to an address that was checked, so DNS cannot answer differently between
/// the check and the connection. Without <c>allowPrivate</c>, a host with any private address is refused:
/// loopback, RFC 1918, link-local, CGNAT 100.64/10, IETF protocol assignments 192.0.0/24, benchmarking 198.18/15,
/// multicast and reserved 224/3, ULA fc00::/7, the unspecified addresses (which reach the box itself on Linux),
/// IPv4-compatible ::/96, and IPv4-mapped, NAT64 and 6to4 forms of the IPv4 ones.
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

    // Blocks from IANA's IPv4 and IPv6 Special-Purpose Address Registries (read 2026-11-23,
    // https://www.iana.org/assignments/iana-ipv4-special-registry and .../iana-ipv6-special-registry) that a mail
    // server on the internet never has, plus multicast 224.0.0.0/4 (IANA's multicast registry). The documentation
    // blocks are left out: nothing routes them. 64:ff9b::/96 (NAT64, RFC 6052) and 2002::/16 (6to4, RFC 3056) carry an
    // IPv4 address that a translator or relay would reach, so that address is what is checked.
    internal static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsPrivateV4(bytes);

        if (bytes.Take(12).All(b => b == 0))
            return true; // ::/96: the unspecified and loopback addresses, and the deprecated IPv4-compatible ones
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes.Skip(4).Take(8).All(b => b == 0))
            return IsPrivateV4(bytes[12..16]);
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsPrivateV4(bytes[2..6]);

        return address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || (bytes[0] & 0xFE) == 0xFC;
    }

    private static bool IsPrivateV4(byte[] bytes) =>
        bytes[0] is 0 or 10 or 127 or >= 224
        || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
        || (bytes[0] == 192 && bytes[1] == 168)
        || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
        || (bytes[0] == 198 && (bytes[1] & 0xFE) == 18)
        || (bytes[0] == 169 && bytes[1] == 254)
        || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64);

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
