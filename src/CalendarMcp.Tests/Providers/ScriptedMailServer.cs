using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CalendarMcp.Core.Providers;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// A listener that answers from a script instead of being a mail server. GreenMail always advertises AUTH and,
/// with authentication disabled, takes any login, so a server that offers no AUTH at all, or one that accepts the
/// connection and never speaks, has to be written. Only what the script names is answered.
/// </summary>
internal sealed class ScriptedMailServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _greeting;
    private readonly Func<string, string?> _answer;
    private readonly ConcurrentQueue<string> _heard = new();
    private int _connections;

    private ScriptedMailServer(string? greeting, Func<string, string?> answer)
    {
        _greeting = greeting;
        _answer = answer;
        _listener.Start();
        _ = AcceptAsync();
    }

    public (string Host, int Port) Address => ("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);

    /// <summary>How many clients connected.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Every line a client sent, in order.</summary>
    public IReadOnlyCollection<string> Heard => _heard;

    /// <summary>An SMTP server that offers AUTH PLAIN, takes any login and accepts any mail, so a send runs to its end.</summary>
    public static ScriptedMailServer SmtpWithAuth()
    {
        var inData = false;
        return new("220 scripted ESMTP", line =>
        {
            if (inData)
            {
                inData = line != ".";
                return inData ? null : "250 queued";
            }

            var verb = line.Split(' ')[0].ToUpperInvariant();
            inData = verb == "DATA";
            return verb switch
            {
                "EHLO" => "250-scripted\r\n250 AUTH PLAIN",
                "AUTH" => "235 accepted",
                "MAIL" or "RCPT" => "250 ok",
                "DATA" => "354 go on",
                "QUIT" => "221 bye",
                _ => "502 not scripted"
            };
        });
    }

    /// <summary>An SMTP server whose EHLO lists no AUTH line, so a client never logs in.</summary>
    public static ScriptedMailServer SmtpWithoutAuth() => new("220 scripted ESMTP", line =>
        line.Split(' ')[0].ToUpperInvariant() switch
        {
            "EHLO" => "250-scripted\r\n250 8BITMIME",
            "HELO" => "250 scripted",
            "QUIT" => "221 bye",
            _ => "502 not scripted"
        });

    /// <summary>
    /// An IMAP server that offers only SCRAM-SHA-256 and answers the client's first SCRAM message with a valid
    /// challenge, so the client prepares the password (RFC 4013 SASLprep) as a real SCRAM login would. It never
    /// completes the login: what is under test is what the client does with the password.
    /// </summary>
    public static ScriptedMailServer ImapWithScram()
    {
        var tag = "";
        return new("* OK [CAPABILITY IMAP4rev1 AUTH=SCRAM-SHA-256] scripted", line =>
        {
            var words = line.Split(' ');
            if (words is [_, "AUTHENTICATE", ..])
            {
                tag = words[0];
                return "+ ";
            }

            if (words is [_, "LOGOUT"])
                return $"* BYE\r\n{words[0]} OK bye";

            if (line == "*")
                return $"{tag} BAD cancelled";

            var clientFirst = Encoding.UTF8.GetString(Convert.FromBase64String(line));
            var clientNonce = clientFirst.Split(',').Single(part => part.StartsWith("r=", StringComparison.Ordinal))[2..];
            var serverFirst = $"r={clientNonce}scripted,s={Convert.ToBase64String("salt"u8.ToArray())},i=4096";
            return $"+ {Convert.ToBase64String(Encoding.UTF8.GetBytes(serverFirst))}";
        });
    }

    /// <summary>An IMAP server with one empty inbox that takes any login: all a verification asks of it.</summary>
    public static ScriptedMailServer ImapWithEmptyInbox() => new("* OK [CAPABILITY IMAP4rev1 UIDPLUS] scripted", line =>
    {
        var words = line.Split(' ');
        var tag = words[0];
        return words.ElementAtOrDefault(1)?.ToUpperInvariant() switch
        {
            "CAPABILITY" => $"* CAPABILITY IMAP4rev1 UIDPLUS\r\n{tag} OK done",
            "LOGIN" => $"{tag} OK [CAPABILITY IMAP4rev1 UIDPLUS] logged in",
            "LIST" => $"* LIST (\\HasNoChildren) \"/\" INBOX\r\n{tag} OK done",
            "EXAMINE" or "SELECT" => $"* 0 EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY 1] ok\r\n* OK [UIDNEXT 1] ok\r\n{tag} OK [READ-ONLY] done",
            "LOGOUT" => $"* BYE\r\n{tag} OK bye",
            _ => $"{tag} BAD not scripted"
        };
    });

    /// <summary>
    /// A guard that resolves <see cref="PublicImapHost"/> to a public address this server answers on, and every
    /// other host to 10.0.0.5, which nothing answers on: the IMAP step passes and the SMTP host is a private one.
    /// </summary>
    public HostGuard GuardWithPrivateSmtp() => new(
        (host, _) => Task.FromResult(new[] { IPAddress.Parse(host == PublicImapHost ? "203.0.113.10" : "10.0.0.5") }),
        async (endpoint, ct) =>
        {
            if (endpoint.Address.ToString() != "203.0.113.10")
                throw new SocketException((int)SocketError.TimedOut);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Address.Port), ct);
            return socket;
        });

    public const string PublicImapHost = "imap.public.test";

    /// <summary>Accepts the connection and never sends a greeting.</summary>
    public static ScriptedMailServer Silent() => new(null, _ => null);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                _ = ServeAsync(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            if (_greeting is not null)
                await WriteAsync(stream, _greeting);

            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                _heard.Enqueue(line);
                if (_answer(line) is not { } answer)
                    continue;

                await WriteAsync(stream, answer);
                // A server that says BYE hangs up, and a client's LOGOUT waits for that.
                if (answer.StartsWith("* BYE", StringComparison.Ordinal))
                    break;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private Task WriteAsync(NetworkStream stream, string answer) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(answer + "\r\n"), _stop.Token).AsTask();

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
