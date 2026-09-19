using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// A pooled protocol connection: dials the transport, runs the
/// startup/authenticate/ready handshake, and runs model-owned framed exchanges.
/// Disposing while rented returns it to the owning pool with its authenticated
/// server session intact.
/// </summary>
internal sealed class PooledDatabaseConnection : IDatabaseConnection
{
    private readonly DefaultDatabaseClient _owner;
    private readonly IConnectionFactory _connectionFactory;
    private readonly DatabaseConnectionSettings _settings;

    private IConnection? _connection;
    private IProtocolFrameReader? _reader;
    private IProtocolFrameWriter? _writer;
    private bool _isOpen;
    private bool _isRented;
    private bool _isClosed;
    private readonly object _exchangeLock = new();
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _exchangeCompletion;
    private Task? _returnTask;

    internal PooledDatabaseConnection(DefaultDatabaseClient owner, IConnectionFactory connectionFactory, DatabaseConnectionSettings settings, ProtocolMessageFamily family)
    {
        _owner = owner;
        _connectionFactory = connectionFactory;
        _settings = settings;
        Family = family;
        Database = _settings.Database!;
        Principal = _settings.Principal;
    }

    /// <inheritdoc />
    public string Database { get; }

    /// <inheritdoc />
    public string Principal { get; }

    /// <inheritdoc />
    public ProtocolVersion ServerVersion { get; private set; }

    /// <inheritdoc />
    public bool IsOpen => _isOpen && _connection is { State: ConnectionState.Open or ConnectionState.Opening };

    /// <inheritdoc />
    public ProtocolMessageFamily Family { get; }

    internal void MarkRented()
    {
        lock (_exchangeLock)
        {
            _isRented = true;
            _returnTask = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isClosed, this);

        if (_isOpen)
        {
            return;
        }

        _connection = await _connectionFactory.ConnectAsync(_settings.EndPoint!, cancellationToken).ConfigureAwait(false);

        Stream stream = _connection.AsStream();
        var channel = new ProtocolChannel(stream, Family, leaveOpen: true);
        _reader = channel.Reader;
        _writer = channel.Writer;

        var startup = new ProtocolStartupMessage(ProtocolVersion.Current, Database, Principal);
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

        ServerVersion = ProtocolVersion.Current;
        _isOpen = true;
    }

    /// <inheritdoc />
    public async ValueTask<TResult> ExecuteAsync<TResult>(IDatabaseProtocolExchange<TResult> exchange, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (!ReferenceEquals(Family, exchange.Family))
        {
            throw new ArgumentException("The exchange belongs to a different message family.", nameof(exchange));
        }
        CancellationTokenSource operation;
        TaskCompletionSource completion;
        lock (_exchangeLock)
        {
            if (!IsOpen)
            {
                throw new DatabaseClientException(ProtocolErrorCode.Internal, "The connection is not open.");
            }
            ObjectDisposedException.ThrowIf(!_isRented, this);
            if (_operation is not null)
            {
                throw new InvalidOperationException("An exchange is already active on this connection.");
            }
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operation = operation;
            _exchangeCompletion = completion;
        }

        try
        {
            return await exchange.ExecuteAsync(_reader!, _writer!, operation.Token).ConfigureAwait(false);
        }
        catch (DatabaseClientException)
        {
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
        catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
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
        finally
        {
            lock (_exchangeLock)
            {
                _operation = null;
                _exchangeCompletion = null;
                operation.Dispose();
                completion.TrySetResult();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> ExecuteStreamingAsync(IDatabaseStreamingExchange exchange, CancellationToken cancellationToken = default)
        => DatabaseDownloadStream.CreateAsync(this, exchange, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_exchangeLock)
        {
            if (_returnTask is not null)
            {
                return new ValueTask(_returnTask);
            }
            if (!_isRented)
            {
                return ValueTask.CompletedTask;
            }
            _isRented = false;
            _operation?.Cancel();
            _returnTask = ReturnAfterExchangeAsync(_exchangeCompletion?.Task);
            return new ValueTask(_returnTask);
        }
    }

    private async Task ReturnAfterExchangeAsync(Task? completion)
    {
        if (completion is not null)
        {
            await completion.ConfigureAwait(false);
        }
        await _owner.ReturnAsync(this).ConfigureAwait(false);
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
        catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The connection failed while sending a frame.", exception));
        }
    }

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
        catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
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
        _isOpen = false;
        return exception;
    }

    /// <summary>
    /// Translates a handshake error frame into a client exception carrying the
    /// server's wire code; the connection never reached ready, so it is broken.
    /// </summary>
    private DatabaseClientException FromErrorFrame(ProtocolFrame frame)
    {
        ProtocolErrorMessage error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        return MarkBroken(new DatabaseClientException(error.Code, error.Message));
    }
}
