using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Quic;
using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// An HTTP connection projection over a real QUIC transport with a completed stream that its peer
/// never reads. Only the HTTP receive sequence is scripted; delivery, abort and disposal are real.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class WithheldQuicDeliveryConnection : IHttpConnection
{
    private readonly QuicConnectionListener _listener;
    private readonly QuicConnection _client;
    private readonly QuicStream _clientStream;
    private readonly IMultiplexedConnection _connection;
    private readonly IDuplexConnection _stream;
    private readonly FakeHttpConnectionContext _context;
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _disposeLock = new();
    private Task? _disposal;
    private int _abortCount;

    private WithheldQuicDeliveryConnection(
        QuicConnectionListener listener,
        QuicConnection client,
        QuicStream clientStream,
        IMultiplexedConnection connection,
        IDuplexConnection stream,
        bool receiveEndsNaturally)
    {
        _listener = listener;
        _client = client;
        _clientStream = clientStream;
        _connection = connection;
        _stream = stream;
        _context = new FakeHttpConnectionContext(parkAfterExchanges: !receiveEndsNaturally);
    }

    public Task Opened => _opened.Task;

    public Task DisposeStarted => _disposeStarted.Task;

    public Task Disposed => _disposed.Task;

    public int AbortCount => Volatile.Read(ref _abortCount);

    public int GracefulCloseCount => _context.GracefulCloseCount;

    public ConnectionId Id => _connection.Id;

    public HttpProtocol Protocol => HttpProtocol.Http30;

    public EndPoint? LocalEndPoint => _connection.LocalEndPoint;

    public EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

    public ConnectionCapabilities Capabilities => _connection.Capabilities;

    public IConnectionFeatureCollection Features => _connection.Features;

    public ConnectionState State => _connection.State;

    public CancellationToken ConnectionClosed => _connection.ConnectionClosed;

    public static async Task<WithheldQuicDeliveryConnection> CreateAsync(
        X509Certificate2 certificate,
        TimeSpan closeDeliveryTimeout,
        bool receiveEndsNaturally,
        CancellationToken cancellationToken)
    {
        SslApplicationProtocol applicationProtocol = new("cohesion-test");
        QuicConnectionListener listener = await QuicConnectionListener.CreateAsync(options =>
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            options.CloseDeliveryTimeout = closeDeliveryTimeout;
            options.ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = [applicationProtocol],
                EnabledSslProtocols = SslProtocols.Tls13,
            };
        }, cancellationToken);

        QuicConnection? client = null;
        QuicStream? clientStream = null;
        IMultiplexedConnection? connection = null;

        try
        {
            ValueTask<IMultiplexedConnection> accepting = listener.AcceptAsync(cancellationToken);
            client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = listener.EndPoint,
                DefaultStreamErrorCode = 0x100,
                DefaultCloseErrorCode = 0x100,
                InitialReceiveWindowSizes = new QuicReceiveWindowSizes
                {
                    Connection = 16 * 1024,
                    LocallyInitiatedBidirectionalStream = 16 * 1024,
                },
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = [applicationProtocol],
                    EnabledSslProtocols = SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
            }, cancellationToken);
            connection = await accepting;

            // A QUIC stream becomes visible to its peer only when it sends data. End the request
            // direction and consume its byte, but leave the client's response direction unread.
            clientStream = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
            await clientStream.WriteAsync(new byte[] { 1 }, completeWrites: true, cancellationToken);
            IDuplexConnection stream = await connection.AcceptStreamAsync(cancellationToken);
            ReadResult request = await stream.Input.ReadAsync(cancellationToken);
            stream.Input.AdvanceTo(request.Buffer.End);

            // The raw client buffers another 64 KiB without a read. This exceeds that and its
            // advertised credit, while staying inside MsQuic's write buffering, as in #1330's tests.
            byte[] response = new byte[160 * 1024];
            Random.Shared.NextBytes(response);
            const int pieceLength = 16 * 1024;

            for (int offset = 0; offset < response.Length; offset += pieceLength)
            {
                await stream.Output.WriteAsync(response.AsMemory(offset, pieceLength), cancellationToken);
            }

            await stream.Output.CompleteAsync();
            await stream.Input.CompleteAsync();
            return new WithheldQuicDeliveryConnection(listener, client, clientStream, connection, stream, receiveEndsNaturally);
        }
        catch
        {
            if (clientStream is not null)
            {
                await clientStream.DisposeAsync();
            }

            if (client is not null)
            {
                await client.DisposeAsync();
            }

            if (connection is not null)
            {
                connection.Abort();
                await connection.DisposeAsync();
            }

            await listener.DisposeAsync();
            throw;
        }
    }

    public IHttpConnectionContext Open()
    {
        _opened.TrySetResult();
        return _context;
    }

    public ValueTask<IHttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IHttpConnectionContext>(Open());
    }

    public void Abort(Exception? reason = null)
    {
        Interlocked.Increment(ref _abortCount);
        _connection.Abort(reason);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposeStarted.TrySetResult();

        try
        {
            await _connection.DisposeAsync();
            _disposed.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposed.TrySetException(exception);
            throw;
        }
    }

    public async Task ReleaseAsync()
    {
        // Close the withholding peer before awaiting any server-side stream or connection disposal.
        await _clientStream.DisposeAsync();
        await _client.DisposeAsync();
        _connection.Abort();

        try
        {
            await DisposeAsync();
            await _stream.DisposeAsync();
        }
        finally
        {
            await _listener.DisposeAsync();
        }
    }
}
