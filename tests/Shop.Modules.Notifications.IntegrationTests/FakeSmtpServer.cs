using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

namespace Shop.Modules.Notifications.IntegrationTests;

/// <summary>
/// A minimal SMTP server on a free loopback port, so the sender is tested over a real SMTP conversation with no
/// container. It speaks just what MailKit uses here: EHLO, AUTH PLAIN when a user is set, MAIL, RCPT, DATA and QUIT.
/// It never offers STARTTLS, like Mailpit without certificates.
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _user;
    private readonly string? _password;
    private readonly Task _accepting;

    /// <param name="user">With a user, the server offers AUTH PLAIN and refuses mail until a client signs in.</param>
    public FakeSmtpServer(string? user = null, string? password = null)
    {
        _user = user;
        _password = password;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    public int Port { get; }

    public ConcurrentQueue<MimeMessage> Received { get; } = new();

    /// <summary>Every command a client sent, so a test can prove what never went over the wire.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var connection = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        var signedIn = _user is null;

        await writer.WriteLineAsync("220 fake.smtp ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            Commands.Enqueue(line);
            var verb = line.Split(' ')[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                    await writer.WriteLineAsync("250-fake.smtp");
                    if (_user is not null)
                    {
                        await writer.WriteLineAsync("250-AUTH PLAIN");
                    }

                    await writer.WriteLineAsync("250 8BITMIME");
                    break;
                case "AUTH":
                    var parts = line.Split(' ');
                    var response = parts.Length > 2 ? parts[2] : await AskAsync(writer, reader);
                    // PLAIN carries "authorization-id NUL user NUL password", base64 encoded
                    var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(response)).Split('\0');
                    signedIn = credentials.Length == 3 && credentials[1] == _user && credentials[2] == _password;
                    await writer.WriteLineAsync(signedIn ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication credentials invalid");
                    break;
                case "MAIL":
                    await writer.WriteLineAsync(signedIn ? "250 2.1.0 OK" : "530 5.7.0 Authentication required");
                    break;
                case "RCPT":
                case "RSET":
                case "NOOP":
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    Received.Enqueue(await ReadMessageAsync(reader));
                    await writer.WriteLineAsync("250 2.0.0 Queued");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 2.0.0 Bye");
                    return;
                default:
                    await writer.WriteLineAsync("502 5.5.2 Command not recognized");
                    break;
            }
        }
    }

    private static async Task<string> AskAsync(StreamWriter writer, StreamReader reader)
    {
        await writer.WriteLineAsync("334 ");
        return await reader.ReadLineAsync() ?? string.Empty;
    }

    // Lines up to a lone ".", with the extra dot removed from lines that start with one (SMTP dot-stuffing)
    private static async Task<MimeMessage> ReadMessageAsync(StreamReader reader)
    {
        var data = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line && line != ".")
        {
            data.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
        }

        return await MimeMessage.LoadAsync(new MemoryStream(Encoding.UTF8.GetBytes(data.ToString())));
    }
}
