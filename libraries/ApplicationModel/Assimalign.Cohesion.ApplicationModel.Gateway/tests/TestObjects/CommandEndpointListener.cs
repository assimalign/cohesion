using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>One HTTP request as the loopback command endpoint received it.</summary>
/// <param name="Method">The request method.</param>
/// <param name="Target">The request target (path and query) from the request line.</param>
/// <param name="Headers">The request headers, keyed case-insensitively.</param>
/// <param name="Body">The request body bytes.</param>
internal sealed record RecordedCommandRequest(
    string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// A loopback HTTP/1.1 endpoint that records the first request it receives and answers it with a
/// fixed response, optionally over TLS. It exercises the real client transport end to end.
/// </summary>
internal sealed class CommandEndpointListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<RecordedCommandRequest> _received =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _statusCode;
    private readonly string _reasonPhrase;
    private readonly string? _contentType;
    private readonly byte[] _body;
    private readonly X509Certificate2? _certificate;
    private readonly Task _serving;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandEndpointListener"/> class and starts listening.
    /// </summary>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="reasonPhrase">The response reason phrase.</param>
    /// <param name="contentType">The response content type, or <see langword="null"/> for none.</param>
    /// <param name="body">The response body, or <see langword="null"/> for an empty body.</param>
    /// <param name="certificate">The server certificate for TLS, or <see langword="null"/> for plain HTTP.</param>
    public CommandEndpointListener(
        int statusCode = 200, string reasonPhrase = "OK", string? contentType = null, string? body = null,
        X509Certificate2? certificate = null)
    {
        _statusCode = statusCode;
        _reasonPhrase = reasonPhrase;
        _contentType = contentType;
        _body = body is null ? [] : Encoding.UTF8.GetBytes(body);
        _certificate = certificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serving = ServeAsync(_stop.Token);
    }

    /// <summary>Gets the loopback port the endpoint listens on.</summary>
    public int Port { get; }

    /// <summary>Gets the task that completes with the first recorded request.</summary>
    public Task<RecordedCommandRequest> Received => _received.Task;

    /// <summary>Creates a control-plane address on this endpoint.</summary>
    /// <param name="path">The manifest control-plane path.</param>
    /// <returns>The absolute control-plane address.</returns>
    public Uri Address(string path) =>
        new UriBuilder(_certificate is null ? Uri.UriSchemeHttp : Uri.UriSchemeHttps, "127.0.0.1", Port, path).Uri;

    /// <inheritdoc />
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
                await HandleAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Disposal stops the accept loop.
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Stream stream = client.GetStream();
        SslStream? tls = null;
        try
        {
            if (_certificate is not null)
            {
                tls = new SslStream(stream);
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions { ServerCertificate = _certificate }, cancellationToken).ConfigureAwait(false);
                stream = tls;
            }
            RecordedCommandRequest request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            var response = new StringBuilder()
                .Append("HTTP/1.1 ").Append(_statusCode).Append(' ').Append(_reasonPhrase).Append("\r\n")
                .Append("Content-Length: ").Append(_body.Length).Append("\r\n");
            if (_contentType is not null)
            {
                response.Append("Content-Type: ").Append(_contentType).Append("\r\n");
            }
            response.Append("Connection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(_body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _received.TrySetResult(request);
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException)
        {
            // A client that rejects the server certificate aborts the handshake; nothing is recorded.
        }
        finally
        {
            if (tls is not null)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<RecordedCommandRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The client closed the connection before sending request headers.");
            }
            buffer.Write(chunk, 0, read);
            headerEnd = IndexOfHeaderTerminator(buffer.GetBuffer(), (int)buffer.Length);
        }

        byte[] received = buffer.ToArray();
        string[] lines = Encoding.ASCII.GetString(received, 0, headerEnd).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < lines.Length; index++)
        {
            int separator = lines[index].IndexOf(':', StringComparison.Ordinal);
            headers[lines[index][..separator].Trim()] = lines[index][(separator + 1)..].Trim();
        }

        int length = headers.TryGetValue("Content-Length", out string? value)
            ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)
            : 0;
        var body = new MemoryStream();
        body.Write(received, headerEnd + 4, received.Length - headerEnd - 4);
        while (body.Length < length)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The client closed the connection before sending the request body.");
            }
            body.Write(chunk, 0, read);
        }
        return new RecordedCommandRequest(requestLine[0], requestLine[1], headers, body.ToArray());
    }

    private static int IndexOfHeaderTerminator(byte[] data, int length)
    {
        for (int index = 0; index + 3 < length; index++)
        {
            if (data[index] == '\r' && data[index + 1] == '\n' && data[index + 2] == '\r' && data[index + 3] == '\n')
            {
                return index;
            }
        }
        return -1;
    }
}
