using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Internal;

// An authenticated transport. Only the pool owns connectors; callers receive a new lease per rent.
internal sealed class DatabaseConnector
{
    private readonly IDuplexConnectionFactory _connectionFactory;
    private readonly DatabaseConnectionSettings _settings;

    private IDuplexConnection? _connection;
    private ProtocolFrameReader? _reader;
    private ProtocolFrameWriter? _writer;
    private volatile bool _isOpen;
    private bool _isClosed;
    private int _opened;

    internal DatabaseConnector(IDuplexConnectionFactory connectionFactory, DatabaseConnectionSettings settings, ProtocolMessageFamily family)
    {
        _connectionFactory = connectionFactory;
        _settings = settings;
        Family = family;
        Database = _settings.Database!;
        Principal = _settings.Principal;
    }

    /// <summary>
    /// Gets the database this connection is bound to.
    /// </summary>
    public string Database { get; }

    /// <summary>
    /// Gets the principal this connection authenticated as.
    /// </summary>
    public string Principal { get; }

    /// <summary>
    /// Gets the protocol version negotiated with the server.
    /// </summary>
    public ProtocolVersion ServerVersion { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the connection is open and usable.
    /// </summary>
    /// <remarks>
    /// An exchange failure that certified its complete response leaves the connection open; a
    /// protocol or transport failure, a cancellation, or an unfinished response marks it broken.
    /// </remarks>
    public bool IsOpen => _isOpen && _connection is { State: ConnectionState.Open or ConnectionState.Opening };

    /// <summary>Gets the message family fixed when the owning pool was created.</summary>
    public ProtocolMessageFamily Family { get; }

    /// <summary>Gets the capabilities agreed during startup.</summary>
    public IReadOnlyList<string> Capabilities { get; private set; } = Array.Empty<string>();

    internal void Invalidate() => _isOpen = false;

    /// <summary>
    /// Opens the connection: dials the transport and runs the startup, authenticate and ready
    /// handshake. The owning client calls it once, before the first rental.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseClientException">Thrown when the dial fails (<see cref="ProtocolErrorCode.ConnectionFailure"/>, with the factory's exception as the inner exception), the transport breaks during the handshake (<see cref="ProtocolErrorCode.Internal"/>, with the transport's exception as the inner exception), the server breaks the protocol (<see cref="ProtocolErrorCode.ProtocolViolation"/>) or the server rejects the handshake (version, authentication, unknown database, capacity).</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> cancels the dial or the handshake; a dial failure the transport reports after the cancellation is its inner exception.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection, or an object the connection factory needs, is disposed.</exception>
    internal async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isClosed, this);

        if (_isOpen)
        {
            return;
        }

        long startTimestamp = DatabaseClientEventSource.Log.GetTimestamp();

        // A failed open is captured by the filter, which declines it, and written from the finally,
        // once what the open threw from has unwound.
        DatabaseClientException? openFailure = null;
        try
        {
            _connection = await DialAsync(cancellationToken).ConfigureAwait(false);

            Stream stream = _connection.AsStream();
            var channel = new ProtocolChannel(stream, Family, leaveOpen: true);
            _reader = channel.Reader;
            _writer = channel.Writer;

            var startup = new ProtocolStartupMessage(ProtocolVersion.Current, Database, Principal)
            { Capabilities = Family.Capabilities.Select(capability => capability.Name).ToArray() };
            await WriteFrameAsync(ProtocolMessageType.Startup, startup.Encode(), cancellationToken).ConfigureAwait(false);

            ProtocolFrame challenge = await ExpectFrameAsync(cancellationToken).ConfigureAwait(false);

            if (challenge.Type == ProtocolMessageType.Error)
            {
                // Startup rejections: unsupported version, unknown database, capacity.
                throw FromErrorFrame(challenge);
            }

            if (challenge.Type != ProtocolMessageType.Authenticate)
            {
                throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"Expected an authenticate challenge but received {challenge.Type}."));
            }

            // MVP trust method: the challenge carries no payload and the response
            // sends no evidence. Method-specific responses arrive with real
            // authenticator implementations.
            await WriteFrameAsync(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);

            ProtocolFrame ready = await ExpectFrameAsync(cancellationToken).ConfigureAwait(false);

            if (ready.Type == ProtocolMessageType.Error)
            {
                // Authentication rejection.
                throw FromErrorFrame(ready);
            }

            if (ready.Type != ProtocolMessageType.Ready)
            {
                throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"Expected a ready frame but received {ready.Type}."));
            }
            try
            {
                var agreement = ProtocolReadyMessage.Decode(ready.Payload.Span);
                channel.SetAgreement(agreement);
                ServerVersion = agreement.Version;
                Capabilities = channel.Capabilities;
            }
            catch (ProtocolException exception)
            {
                throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, exception.Message, exception));
            }
        }
        catch (DatabaseClientException exception) when (CaptureOpenFailure(exception, out openFailure))
        {
            // Unreachable: the filter records the failure and declines it, so it propagates
            // unchanged.
            throw;
        }
        finally
        {
            if (openFailure is not null)
            {
                DatabaseClientEventSource.Log.ConnectionOpenFailed(this, openFailure, startTimestamp);
            }
        }

        _isOpen = true;

        // The flag the first close exchanges: a connection that opened is counted closed once.
        Volatile.Write(ref _opened, 1);
        DatabaseClientEventSource.Log.ConnectionOpened(this, startTimestamp);
    }

    /// <summary>
    /// Records a failed open and declines it, so the exception filter that calls it never catches;
    /// the open writes the failure from its finally.
    /// </summary>
    /// <returns>Always false.</returns>
    private static bool CaptureOpenFailure(DatabaseClientException exception, out DatabaseClientException? captured)
    {
        captured = exception;
        return false;
    }

    /// <summary>
    /// Dials the transport. A failure of the connection factory (a refused or unreachable
    /// endpoint, a TLS handshake failure, a connect timeout) becomes a
    /// <see cref="DatabaseClientException"/> with <see cref="ProtocolErrorCode.ConnectionFailure"/>,
    /// the endpoint in its message and the factory's exception as its inner exception, the way
    /// Npgsql's connector wraps a failed connect in <c>NpgsqlException</c>.
    /// </summary>
    /// <remarks>
    /// The caller's cancellation and a disposed object propagate unchanged. A failure the
    /// transport reports after the caller canceled (a socket aborted by the cancellation, say)
    /// is the caller's cancellation too: it becomes an <see cref="OperationCanceledException"/>
    /// for the caller's token, with the transport's exception inside, as Npgsql checks the
    /// caller's token before it classifies a failed connect. A cancellation the caller did not
    /// request is the transport's own timeout (the TLS layer cancels a handshake that outlives
    /// its timeout), so it is wrapped as a dial failure too, as Npgsql turns such a cancellation
    /// into a timeout.
    /// </remarks>
    private async ValueTask<IDuplexConnection> DialAsync(CancellationToken cancellationToken)
    {
        EndPoint endPoint = _settings.EndPoint!;

        try
        {
            return await _connectionFactory.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DatabaseClientException(ProtocolErrorCode.ConnectionFailure, $"Failed to connect to {Describe(endPoint)}: the connection attempt timed out.", exception);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not (OperationCanceledException or ObjectDisposedException or OutOfMemoryException))
        {
            throw new OperationCanceledException("The connection attempt was canceled.", exception, cancellationToken);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ObjectDisposedException or OutOfMemoryException))
        {
            // Broad on purpose: IDuplexConnectionFactory is an open seam and each transport throws its
            // own types (SocketException, IOException, AuthenticationException, TimeoutException,
            // ConnectionAbortedException), so a list would let the next transport's failure escape.
            throw new DatabaseClientException(ProtocolErrorCode.ConnectionFailure, $"Failed to connect to {Describe(endPoint)}: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Names an endpoint the way a reader writes it: <c>host:port</c> for a
    /// <see cref="DnsEndPoint"/> (whose own text adds the address family, as in
    /// <c>Unspecified/host:port</c>), with an IPv6 literal in brackets, and the endpoint's own
    /// text otherwise.
    /// </summary>
    private static string Describe(EndPoint endPoint)
        => endPoint switch
        {
            DnsEndPoint dns when dns.Host.Contains(':') && !dns.Host.StartsWith('[') => $"[{dns.Host}]:{dns.Port}",
            DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
            _ => endPoint.ToString() ?? endPoint.GetType().Name,
        };

    /// <summary>
    /// Names the endpoint the connection dials, for the event source.
    /// </summary>
    internal string DescribeEndPoint() => Describe(_settings.EndPoint!);

    internal async ValueTask<TResult> ExecuteAsync<TResult>(DatabaseProtocolExchange<TResult> exchange, CancellationToken cancellationToken = default)
    {
        try
        {
            return await exchange.ExecuteAsync(_reader!, _writer!, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseClientException exception) when (exception.Code == ProtocolErrorCode.ConnectionFailure)
        {
            // Only the dial raises the client-local code, so an exchange that reports it decoded it
            // from a server's error frame: a peer that is not a conforming server. The decoded
            // exception is not kept as the inner exception, so no exception chain carries the code
            // for anything but a failed dial.
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"The server sent the client-local {nameof(ProtocolErrorCode.ConnectionFailure)} code: {exception.Message}"));
        }
        catch (DatabaseClientException exception)
        {
            DatabaseClientEventSource.Log.ExchangeFailed(this, exception);

            // The exchange knows whether it consumed a terminal, reusable response.
            if (!exchange.IsResponseComplete)
            {
                _isOpen = false;
            }
            throw;
        }
        catch (ProtocolException exception)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, exception.Message, exception));
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The connection failed during an exchange.", exception));
        }
        catch (OperationCanceledException)
        {
            // Cancellation or a failed decoder may leave an unfinished response.
            _isOpen = false;
            throw;
        }
        catch
        {
            if (!exchange.IsResponseComplete)
            {
                _isOpen = false;
            }
            throw;
        }
    }

    /// <summary>
    /// Closes the wire connection for real: best-effort terminate frame, then
    /// transport teardown. Idempotent.
    /// </summary>
    internal async ValueTask CloseAsync()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;

        // _isClosed is checked and set without synchronization, so the gauge counts the close of
        // an opened connection behind its own exchange.
        if (Interlocked.Exchange(ref _opened, 0) == 1)
        {
            DatabaseClientEventSource.Log.ConnectionClosed(this);
        }

        if (_isOpen && _writer is not null)
        {
            _isOpen = false;

            try
            {
                await _writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Terminate, ReadOnlyMemory<byte>.Empty)).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Best effort; the server may already be gone.
            }
        }

        _isOpen = false;

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask WriteFrameAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        try
        {
            await _writer!.WriteFrameAsync(new ProtocolFrame(type, payload), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The connection failed while sending a frame.", exception));
        }
    }

    /// <summary>
    /// Tells whether an exception is the transport failing under an open connection. A
    /// <see cref="SocketException"/> is one of them: the TCP transport completes the connection's
    /// input with the raw socket error when the peer resets the connection, and a
    /// <see cref="SocketException"/> is not an <see cref="IOException"/>.
    /// </summary>
    private static bool IsTransportFailure(Exception exception)
        => exception is IOException or SocketException or ConnectionAbortedException or ConnectionResetException;

    /// <summary>
    /// Reads the next frame, translating transport failures and unexpected
    /// end-of-stream into client exceptions that mark the connection broken.
    /// </summary>
    private async ValueTask<ProtocolFrame> ExpectFrameAsync(CancellationToken cancellationToken)
    {
        ProtocolFrame? frame;

        try
        {
            frame = await _reader!.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolException exception)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, exception.Message, exception));
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The connection failed while awaiting a frame.", exception));
        }

        if (frame is null)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The server closed the connection mid-exchange."));
        }

        return frame.Value;
    }

    private DatabaseClientException MarkBroken(DatabaseClientException exception)
    {
        // An open that fails never opened, so its failure is ConnectionOpenFailed, not a break.
        if (_isOpen)
        {
            DatabaseClientEventSource.Log.ConnectionBroken(this, exception);
        }

        _isOpen = false;
        return exception;
    }

    /// <summary>
    /// Translates a handshake error frame into a client exception carrying the
    /// server's wire code; the connection never reached ready, so it is broken.
    /// </summary>
    /// <remarks>
    /// A malformed error payload, or one that carries the client-local
    /// <see cref="ProtocolErrorCode.ConnectionFailure"/> code no conforming server sends, is a
    /// <see cref="ProtocolErrorCode.ProtocolViolation"/>, so the code keeps meaning a failed dial.
    /// </remarks>
    private DatabaseClientException FromErrorFrame(ProtocolFrame frame)
    {
        ProtocolErrorMessage error;

        try
        {
            error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        }
        catch (ProtocolException exception)
        {
            return MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, exception.Message, exception));
        }

        if (error.Code == ProtocolErrorCode.ConnectionFailure)
        {
            return MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"The server sent the client-local {nameof(ProtocolErrorCode.ConnectionFailure)} code: {error.Message}"));
        }

        return MarkBroken(new DatabaseClientException(error.Code, error.Message));
    }
}
