using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Internal;
using Assimalign.Cohesion.Connections.NamedPipes.Internal;

namespace Assimalign.Cohesion.Connections.NamedPipes;

/// <summary>
/// Listens for inbound, reliable, ordered single-stream named-pipe connections on a local pipe name
/// (the Windows-native local IPC equivalent of a Unix domain socket listener).
/// </summary>
/// <remarks>
/// The first call to <see cref="AcceptAsync(CancellationToken)"/> creates the initial server instance,
/// reserving the pipe name; each subsequent accept creates a fresh <see cref="NamedPipeServerStream"/>
/// instance that shares the name, so the listener keeps serving new clients as prior connections stay
/// live. Access control is applied at creation time via <see cref="NamedPipeConnectionListenerOptions.PipeSecurity"/>
/// (Windows) or <see cref="NamedPipeConnectionListenerOptions.CurrentUserOnly"/>.
/// </remarks>
public sealed class NamedPipeConnectionListener : ConnectionListener
{
    private readonly NamedPipeConnectionListenerOptions _options;
    private readonly NamedPipeEndPoint _endPoint;
    private readonly ListenerId _listenerId = ListenerId.New();
    private readonly ConcurrentDictionary<ConnectionId, NamedPipeConnection> _connections = new();
    private readonly Lock _gate = new();

    private NamedPipeServerStream? _pendingStream;
    private bool _isBound;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeConnectionListener"/> class.
    /// </summary>
    /// <param name="options">The binding and pipe-tuning options for the listener.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="NamedPipeConnectionListenerOptions.EndPoint"/> is <see langword="null"/> or
    /// names a pipe on a non-local host (a server can only be created on the local host).
    /// </exception>
    public NamedPipeConnectionListener(NamedPipeConnectionListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.EndPoint is null)
        {
            throw new ArgumentException("A named-pipe listener requires an endpoint.", nameof(options));
        }

        if (!options.EndPoint.IsLocal)
        {
            throw new ArgumentException(
                "A named-pipe listener can only bind a pipe on the local host ('.').",
                nameof(options));
        }

        _options = options;
        _endPoint = options.EndPoint;
    }

    /// <inheritdoc />
    public override EndPoint EndPoint => _endPoint;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.NamedPipe,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    /// <inheritdoc />
    public override async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;

            lock (_gate)
            {
                if (_isDisposed)
                {
                    break;
                }

                server = CreateServerStream();
                _pendingStream = server;

                if (!_isBound)
                {
                    _isBound = true;
                    ConnectionEventSource.Log.ListenerInitialized(ConnectionProtocol.NamedPipe, _listenerId);
                }
            }

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (IOException)
            {
                // The client aborted before the handshake completed; retry with a fresh instance.
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            catch (ObjectDisposedException)
            {
                // The listener was disposed while this accept was waiting.
                continue;
            }

            NamedPipeConnection connection = new(server, _endPoint, _endPoint);

            lock (_gate)
            {
                _pendingStream = null;

                if (_isDisposed)
                {
                    // The listener was disposed after the client connected but before it was tracked.
                    _ = connection.DisposeAsync();
                    break;
                }

                _connections.TryAdd(connection.Id, connection);
            }

            connection.ConnectionClosed.Register(static state =>
            {
                (NamedPipeConnectionListener listener, NamedPipeConnection closed) =
                    ((NamedPipeConnectionListener, NamedPipeConnection))state!;

                listener._connections.TryRemove(closed.Id, out _);

            }, (this, connection));

            ConnectionEventSource.Log.ConnectionStart(ConnectionProtocol.NamedPipe, _listenerId, connection.Id);

            return connection;
        }

        throw new OperationCanceledException(
            "The named-pipe listener has been disposed.",
            cancellationToken);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        NamedPipeServerStream? pending;

        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            pending = _pendingStream;
            _pendingStream = null;
        }

        // Disposing the in-flight listening instance unblocks a pending WaitForConnectionAsync.
        if (pending is not null)
        {
            await pending.DisposeAsync().ConfigureAwait(false);
        }

        // ConcurrentDictionary.Values is a snapshot, so connections removing themselves as they close
        // do not invalidate the iteration.
        foreach (NamedPipeConnection connection in _connections.Values)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
    }

    /// <summary>
    /// Creates a new <see cref="NamedPipeConnectionListener"/> configured by the supplied delegate.
    /// </summary>
    /// <param name="configure">A delegate used to configure the listener options.</param>
    /// <returns>A new <see cref="NamedPipeConnectionListener"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static NamedPipeConnectionListener Create(Action<NamedPipeConnectionListenerOptions> configure)
        => new(NamedPipeConnectionListenerOptions.Create(configure));

    private NamedPipeServerStream CreateServerStream()
    {
        PipeOptions pipeOptions = PipeOptions.Asynchronous;

        if (_options.WriteThrough)
        {
            pipeOptions |= PipeOptions.WriteThrough;
        }

        if (OperatingSystem.IsWindows())
        {
            NamedPipeServerStream? secured = TryCreateSecuredServerStream(pipeOptions);

            if (secured is not null)
            {
                return secured;
            }
        }

        if (_options.CurrentUserOnly)
        {
            pipeOptions |= PipeOptions.CurrentUserOnly;
        }

        return new NamedPipeServerStream(
            _endPoint.PipeName,
            PipeDirection.InOut,
            _options.MaxServerInstances,
            PipeTransmissionMode.Byte,
            pipeOptions,
            _options.InputBufferSize,
            _options.OutputBufferSize);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream? TryCreateSecuredServerStream(PipeOptions pipeOptions)
    {
        if (_options.PipeSecurity is null)
        {
            return null;
        }

        // An explicit ACL fully specifies access, so CurrentUserOnly (which sets its own ACL) is not
        // combined with it.
        return NamedPipeServerStreamAcl.Create(
            _endPoint.PipeName,
            PipeDirection.InOut,
            _options.MaxServerInstances,
            PipeTransmissionMode.Byte,
            pipeOptions,
            _options.InputBufferSize,
            _options.OutputBufferSize,
            _options.PipeSecurity);
    }
}
