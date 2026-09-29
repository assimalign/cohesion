using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>One request as the loopback store received it.</summary>
/// <param name="Method">The request method.</param>
/// <param name="Target">The request target (path and query) from the request line.</param>
/// <param name="Authorization">The <c>Authorization</c> header value, or <see langword="null"/>.</param>
internal sealed record LoopbackStoreRequest(string Method, string Target, string? Authorization);

/// <summary>
/// A plain-HTTP loopback test double for a store's control plane: it answers each request target
/// with a configured body (404 otherwise), closes every connection after one response, and records
/// what it received. It lets the real orchestration providers, with their real HTTP transport, run
/// against a gateway without a store host.
/// </summary>
internal sealed class LoopbackStore : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, (string ContentType, byte[] Body)> _responses = new(StringComparer.Ordinal);
    private readonly List<LoopbackStoreRequest> _requests = new();
    private readonly object _gate = new();
    private readonly Task _serving;

    public LoopbackStore()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serving = ServeAsync(_stop.Token);
    }

    public int Port { get; }

    public IReadOnlyList<LoopbackStoreRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Answers requests for <paramref name="target"/> with <paramref name="body"/>.</summary>
    /// <param name="target">The exact request target, for example <c>/cohesion/v1/secrets?path=key</c>.</param>
    /// <param name="contentType">The response content type.</param>
    /// <param name="body">The response body.</param>
    public void Respond(string target, string contentType, byte[] body)
    {
        lock (_gate)
        {
            _responses[target] = (contentType, body);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _serving.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await HandleAsync(client.GetStream(), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // A client that goes away mid-request records nothing.
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Disposal stops the accept loop.
        }
    }

    private async Task HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        string[] lines = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
        string[] requestLine = lines[0].Split(' ');
        string? authorization = null;
        for (int index = 1; index < lines.Length; index++)
        {
            int separator = lines[index].IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 &&
                string.Equals(lines[index][..separator].Trim(), "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                authorization = lines[index][(separator + 1)..].Trim();
            }
        }

        (string ContentType, byte[] Body) response;
        bool found;
        lock (_gate)
        {
            _requests.Add(new LoopbackStoreRequest(requestLine[0], requestLine[1], authorization));
            found = _responses.TryGetValue(requestLine[1], out response);
        }

        byte[] body = found ? response.Body : [];
        string head = (found ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 404 Not Found\r\n") +
            (found ? $"Content-Type: {response.ContentType}\r\n" : string.Empty) +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string[]> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The client closed the connection before sending request headers.");
            }

            buffer.Write(chunk, 0, read);
            string text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return text[..end].Split("\r\n");
            }
        }
    }
}
