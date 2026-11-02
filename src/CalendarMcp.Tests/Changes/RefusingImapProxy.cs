using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// Sits between a client and a real server and refuses a mailbox when a test says so. Neither GreenMail nor
/// Dovecot can be made to answer a temporary NO on demand, and that is the answer under test. Everything
/// else passes through untouched, so the rest of the conversation is still the real server's.
/// </summary>
internal sealed class RefusingImapProxy : IAsyncDisposable
{
    private readonly (string Host, int Port) _server;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    private RefusingImapProxy((string Host, int Port) server) => _server = server;

    public (string Host, int Port) Address => ("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);

    /// <summary>What to answer an EXAMINE of the Sent folder, such as <c>NO [UNAVAILABLE] Try later</c>. Null passes it on.</summary>
    public string? Refusal { get; set; }

    /// <summary>Lists the Sent folder with <c>\Noselect</c>, as a server does for a folder that only holds others.</summary>
    public bool Noselect { get; set; }

    public int Refused { get; private set; }

    public static RefusingImapProxy Before((string Host, int Port) server)
    {
        var proxy = new RefusingImapProxy(server);
        proxy._listener.Start();
        _ = proxy.AcceptAsync();
        return proxy;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = RelayAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using var server = new TcpClient();
        using var _ = client;

        try
        {
            await server.ConnectAsync(_server.Host, _server.Port, _stop.Token);
            var toClient = client.GetStream();
            var toServer = server.GetStream();

            // Either side going means both: a server drops a session whose folder was deleted, and the
            // client has to see that to connect again.
            await Task.WhenAny(RelayAnswersAsync(toServer, toClient), RelayCommandsAsync(toClient, toServer));
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
        }
    }

    private async Task RelayAnswersAsync(NetworkStream server, NetworkStream client)
    {
        await foreach (var line in LinesAsync(server))
            await client.WriteAsync(Listed(line), _stop.Token);
    }

    private byte[] Listed(byte[] line)
    {
        var text = Encoding.ASCII.GetString(line);
        if (!Noselect || !text.StartsWith("* LIST (", StringComparison.Ordinal)
            || text.TrimEnd().Trim('"').Split(' ', '"')[^1] != ImapMailbox.SentFolder)
        {
            return line;
        }

        return Encoding.ASCII.GetBytes(text.Replace("* LIST (", "* LIST (\\Noselect ", StringComparison.Ordinal));
    }

    private async Task RelayCommandsAsync(NetworkStream client, NetworkStream server)
    {
        await foreach (var line in LinesAsync(client))
        {
            if (Refusing(line) is { } refusal)
                await client.WriteAsync(refusal, _stop.Token);
            else
                await server.WriteAsync(line, _stop.Token);
        }
    }

    private byte[]? Refusing(byte[] line)
    {
        var parts = Encoding.ASCII.GetString(line).TrimEnd().Split(' ', 3);
        if (Refusal is null || parts.Length < 3 || !parts[1].Equals("EXAMINE", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Trim('"').StartsWith(ImapMailbox.SentFolder, StringComparison.Ordinal))
        {
            return null;
        }

        Refused++;
        return Encoding.ASCII.GetBytes($"{parts[0]} {Refusal}\r\n");
    }

    private async IAsyncEnumerable<byte[]> LinesAsync(NetworkStream stream)
    {
        var line = new List<byte>();
        var chunk = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, _stop.Token);
            if (read == 0)
                yield break;

            foreach (var octet in chunk.AsMemory(0, read).ToArray())
            {
                line.Add(octet);
                if (octet == (byte)'\n')
                {
                    yield return line.ToArray();
                    line.Clear();
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
