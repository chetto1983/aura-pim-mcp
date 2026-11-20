using System.Net;
using System.Net.Sockets;
using System.Text;

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

    private ScriptedMailServer(string? greeting, Func<string, string?> answer)
    {
        _greeting = greeting;
        _answer = answer;
        _listener.Start();
        _ = AcceptAsync();
    }

    public (string Host, int Port) Address => ("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);

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

    /// <summary>Accepts the connection and never sends a greeting.</summary>
    public static ScriptedMailServer Silent() => new(null, _ => null);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
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
                if (_answer(line) is { } answer)
                    await WriteAsync(stream, answer);
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
