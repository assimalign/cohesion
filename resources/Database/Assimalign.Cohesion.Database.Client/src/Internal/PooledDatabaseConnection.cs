using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// A pooled protocol connection: dials the transport, runs the
/// startup/authenticate/ready handshake, executes statements, and drains result
/// streams. Disposing while rented returns it to the owning pool with its
/// authenticated server session intact.
/// </summary>
internal sealed class PooledDatabaseConnection : IDatabaseConnection
{
    private readonly DefaultDatabaseClient _owner;
    private readonly IConnectionFactory _connectionFactory;
    private readonly DatabaseConnectionSettings _settings;
    private readonly DatabaseKeyWriter _parameterWriter = new();

    private IConnection? _connection;
    private IProtocolFrameReader? _reader;
    private IProtocolFrameWriter? _writer;
    private bool _isOpen;
    private bool _isRented;
    private bool _isClosed;

    internal PooledDatabaseConnection(DefaultDatabaseClient owner, IConnectionFactory connectionFactory, DatabaseConnectionSettings settings)
    {
        _owner = owner;
        _connectionFactory = connectionFactory;
        _settings = settings;
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

    internal void MarkRented() => _isRented = true;

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
        _reader = ProtocolFraming.CreateReader(stream, leaveOpen: true);
        _writer = ProtocolFraming.CreateWriter(stream, leaveOpen: true);

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
    public async ValueTask<DatabaseClientResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        if (!IsOpen)
        {
            throw new DatabaseClientException(ProtocolErrorCode.Internal, "The connection is not open.");
        }

        var encodedParameters = new Dictionary<string, byte[]>(parameters?.Count ?? 0);

        if (parameters is not null)
        {
            foreach ((string name, object? value) in parameters)
            {
                _parameterWriter.Reset();
                DatabaseValueCodec.Append(_parameterWriter, value);
                encodedParameters[name] = _parameterWriter.ToArray();
            }
        }

        var execute = new ProtocolExecuteMessage(statement, encodedParameters);
        await WriteFrameAsync(ProtocolMessageType.Execute, execute.Encode(), cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DatabaseClientColumn> columns = [];
        var rows = new List<object?[]>();

        while (true)
        {
            ProtocolFrame frame = await ExpectFrameAsync(cancellationToken).ConfigureAwait(false);

            switch (frame.Type)
            {
                case ProtocolMessageType.ResultHeader:
                {
                    ProtocolResultHeaderMessage header = ProtocolResultHeaderMessage.Decode(frame.Payload.Span);
                    var decoded = new List<DatabaseClientColumn>(header.Columns.Count);

                    foreach ((string name, byte type) in header.Columns)
                    {
                        decoded.Add(new DatabaseClientColumn(name, (DatabaseType)type));
                    }

                    columns = decoded;
                    break;
                }

                case ProtocolMessageType.ResultRow:
                {
                    rows.Add(DecodeRow(frame.Payload.Span, columns.Count));
                    break;
                }

                case ProtocolMessageType.ResultComplete:
                {
                    ProtocolResultCompleteMessage complete = ProtocolResultCompleteMessage.Decode(frame.Payload.Span);
                    return new DatabaseClientResult(columns, rows, complete.AffectedCount);
                }

                case ProtocolMessageType.Error:
                {
                    ProtocolErrorMessage error = ProtocolErrorMessage.Decode(frame.Payload.Span);

                    // Statement-level failures leave the server session in the
                    // ready state, so the connection stays poolable; everything
                    // else means the server is closing the session.
                    if (error.Code is not (ProtocolErrorCode.ParseFailure or ProtocolErrorCode.ExecutionFailure))
                    {
                        _isOpen = false;
                    }

                    throw new DatabaseClientException(error.Code, error.Message);
                }

                default:
                    throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"Unexpected {frame.Type} frame in an execute exchange."));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isRented)
        {
            _isRented = false;
            await _owner.ReturnAsync(this).ConfigureAwait(false);
            return;
        }

        await CloseAsync().ConfigureAwait(false);
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

    private static object?[] DecodeRow(ReadOnlySpan<byte> payload, int columnCount)
    {
        var values = new List<object?>(columnCount);
        var reader = new DatabaseKeyReader(payload);

        while (!reader.IsAtEnd)
        {
            values.Add(DatabaseValueCodec.Read(ref reader));
        }

        return [.. values];
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
