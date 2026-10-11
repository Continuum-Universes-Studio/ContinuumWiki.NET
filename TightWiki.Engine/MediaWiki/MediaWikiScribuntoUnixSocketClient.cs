using System.Net.Sockets;
using System.Text;

namespace TightWiki.Engine.MediaWiki;

/// <summary>Bounded, line-framed client for the administrator-owned worker socket.</summary>
public sealed class MediaWikiScribuntoUnixSocketClient
{
    private readonly string _socketPath;
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _executionTimeout;
    private readonly int _maxRequestCharacters;
    private readonly int _maxResponseCharacters;

    public MediaWikiScribuntoUnixSocketClient(string socketPath, TimeSpan connectionTimeout, TimeSpan executionTimeout,
        int maxRequestCharacters = 2_000_000, int maxResponseCharacters = 100_000)
    {
        if (string.IsNullOrWhiteSpace(socketPath) || !Path.IsPathFullyQualified(socketPath)) throw new ArgumentException("A fully qualified socket path is required.", nameof(socketPath));
        if (connectionTimeout <= TimeSpan.Zero || executionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
        if (maxRequestCharacters is < 1 or > 2_000_000 || maxResponseCharacters is < 1 or > 250_000) throw new ArgumentOutOfRangeException(nameof(maxRequestCharacters));
        _socketPath = socketPath; _connectionTimeout = connectionTimeout; _executionTimeout = executionTimeout;
        _maxRequestCharacters = maxRequestCharacters; _maxResponseCharacters = maxResponseCharacters;
    }

    public async Task<string> SendAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestJson) || requestJson.Contains('\n') || requestJson.Length > _maxRequestCharacters)
            throw new InvalidDataException("The worker request exceeded the configured protocol boundary.");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCts.CancelAfter(_connectionTimeout);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), connectionCts.Token);
        var payload = Encoding.UTF8.GetBytes(requestJson + "\n");
        await socket.SendAsync(payload, SocketFlags.None, cancellationToken);
        socket.Shutdown(SocketShutdown.Send);
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        executionCts.CancelAfter(_executionTimeout);
        var buffer = new byte[Math.Min(8192, _maxResponseCharacters + 1)];
        var output = new MemoryStream();
        while (true)
        {
            var count = await socket.ReceiveAsync(buffer, SocketFlags.None, executionCts.Token);
            if (count == 0) break;
            if (output.Length + count > _maxResponseCharacters) throw new InvalidDataException("The worker response exceeded the configured protocol boundary.");
            output.Write(buffer, 0, count);
        }
        var response = Encoding.UTF8.GetString(output.ToArray()).Trim();
        if (response.Length == 0 || response.Contains('\n')) throw new InvalidDataException("The worker response framing was invalid.");
        return response;
    }
}
