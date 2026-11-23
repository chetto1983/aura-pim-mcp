using System.Net;
using System.Net.Sockets;
using CalendarMcp.Core.Providers;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// A member must not reach the box's own network through a mail host. The guard resolves once, refuses when any
/// address is private, and connects the address it checked, so a second DNS answer cannot swap it.
/// </summary>
[TestClass]
public class HostGuardTests
{
    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("127.8.9.10")]
    [DataRow("10.1.2.3")]
    [DataRow("172.16.0.1")]
    [DataRow("172.31.255.254")]
    [DataRow("192.168.1.1")]
    [DataRow("169.254.169.254")]
    [DataRow("100.64.0.1")]
    [DataRow("100.127.255.254")]
    [DataRow("0.0.0.0")]
    [DataRow("::1")]
    [DataRow("::")]
    [DataRow("fe80::1")]
    [DataRow("fc00::1")]
    [DataRow("fd12:3456::1")]
    [DataRow("::ffff:127.0.0.1")]
    [DataRow("::ffff:10.0.0.1")]
    [DataRow("::ffff:192.168.0.1")]
    // IANA special-purpose blocks (see HostGuard.IsPrivate) and multicast.
    [DataRow("198.18.0.1")]
    [DataRow("198.19.255.254")]
    [DataRow("192.0.0.8")]
    [DataRow("192.0.0.170")]
    [DataRow("224.0.0.1")]
    [DataRow("239.255.255.250")]
    [DataRow("240.0.0.1")]
    [DataRow("255.255.255.255")]
    [DataRow("::10.0.0.5")]
    [DataRow("::127.0.0.1")]
    [DataRow("64:ff9b::10.0.0.5")]
    [DataRow("64:ff9b::7f00:1")]
    [DataRow("2002:0a00:0005::1")]
    [DataRow("2002:7f00:0001::1")]
    public async Task Refuses127001AndRfc1918AndMappedV6(string address)
    {
        var connected = new List<IPEndPoint>();
        var guard = new HostGuard(Resolving(address), Recording(connected));

        await Assert.ThrowsExactlyAsync<PrivateAddressException>(() =>
            guard.ConnectAsync("mail.example.com", 993, allowPrivate: false, default));

        Assert.IsEmpty(connected, "a refused host is never connected");
    }

    [TestMethod]
    public async Task OnePrivateAddressAmongPublicOnes_IsRefused()
    {
        var connected = new List<IPEndPoint>();
        var guard = new HostGuard(Resolving("203.0.113.7", "10.0.0.5"), Recording(connected));

        await Assert.ThrowsExactlyAsync<PrivateAddressException>(() =>
            guard.ConnectAsync("mail.example.com", 993, allowPrivate: false, default));

        Assert.IsEmpty(connected);
    }

    [TestMethod]
    [DataRow("100.63.255.255")]
    [DataRow("100.128.0.0")]
    [DataRow("172.15.255.255")]
    [DataRow("172.32.0.0")]
    [DataRow("203.0.113.7")]
    [DataRow("2001:db8::1")]
    [DataRow("198.17.255.255")]
    [DataRow("198.20.0.0")]
    [DataRow("192.0.1.1")]
    [DataRow("223.255.255.255")]
    [DataRow("64:ff9b::203.0.113.7")]
    [DataRow("2002:cb00:7107::1")]
    public async Task ANeighbourOfAPrivateRange_IsPublic(string address)
    {
        var connected = new List<IPEndPoint>();
        var guard = new HostGuard(Resolving(address), Recording(connected));

        using var socket = await guard.ConnectAsync("mail.example.com", 993, allowPrivate: false, default);

        Assert.AreEqual(IPAddress.Parse(address), connected.Single().Address);
    }

    [TestMethod]
    public async Task AllowsThemWhenAllowed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            using var socket = await new HostGuard().ConnectAsync("127.0.0.1", port, allowPrivate: true, default);

            Assert.IsTrue(socket.Connected);
            Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, port), socket.RemoteEndPoint);
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task ConnectsThePublicAddressItChecked()
    {
        var resolved = new List<string>();
        var connected = new List<IPEndPoint>();
        var guard = new HostGuard(
            (host, _) =>
            {
                resolved.Add(host);
                return Task.FromResult(new[] { IPAddress.Parse("203.0.113.7") });
            },
            Recording(connected));

        using var socket = await guard.ConnectAsync("mail.example.com", 993, allowPrivate: false, default);

        CollectionAssert.AreEqual(new[] { "mail.example.com" }, resolved, "the host is resolved once");
        Assert.AreEqual(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 993), connected.Single());
    }

    [TestMethod]
    public async Task AMappedAddress_IsConnectedAsIPv4()
    {
        var connected = new List<IPEndPoint>();
        var guard = new HostGuard(Resolving("::ffff:203.0.113.7"), Recording(connected));

        using var socket = await guard.ConnectAsync("mail.example.com", 993, allowPrivate: false, default);

        Assert.AreEqual(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 993), connected.Single());
    }

    [TestMethod]
    public async Task AnAddressThatRefuses_GivesWayToTheNext()
    {
        var connected = new List<IPEndPoint>();
        var guard = new HostGuard(Resolving("203.0.113.7", "203.0.113.8"), (endpoint, ct) =>
        {
            connected.Add(endpoint);
            return endpoint.Address.Equals(IPAddress.Parse("203.0.113.7"))
                ? throw new SocketException((int)SocketError.ConnectionRefused)
                : Recording([])(endpoint, ct);
        });

        using var socket = await guard.ConnectAsync("mail.example.com", 993, allowPrivate: false, default);

        Assert.HasCount(2, connected);
    }

    [TestMethod]
    public async Task NoAddress_IsAHostNotFound()
    {
        var guard = new HostGuard((_, _) => Task.FromResult(Array.Empty<IPAddress>()), Recording([]));

        var refused = await Assert.ThrowsExactlyAsync<SocketException>(() =>
            guard.ConnectAsync("mail.example.com", 993, allowPrivate: false, default));

        Assert.AreEqual(SocketError.HostNotFound, refused.SocketErrorCode);
    }

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolving(params string[] addresses) =>
        (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    // A socket that is never connected stands in for one: these tests are about which address is dialled.
    private static Func<IPEndPoint, CancellationToken, Task<Socket>> Recording(List<IPEndPoint> connected) => (endpoint, _) =>
    {
        connected.Add(endpoint);
        return Task.FromResult(new Socket(SocketType.Stream, ProtocolType.Tcp));
    };
}
