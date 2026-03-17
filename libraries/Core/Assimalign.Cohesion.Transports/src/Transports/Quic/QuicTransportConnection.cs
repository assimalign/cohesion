using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

/// <summary>
/// Represents a QUIC transport connection that can open multiple logical streams.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public sealed class QuicTransportConnection : IMultiplexTransportConnection
{
    private readonly QuicConnection _connection;
    private readonly TransportPipeline _pipeline;
    private readonly QuicStreamType _outboundStreamType;
    private readonly long _defaultCloseErrorCode;
    private readonly TransportStreamPipeOptionsContext _streamOptions;
    private readonly List<QuicTransportContext> _contexts;
    private readonly Lock _contextsLock;
    private readonly Lock _stateLock;

    private volatile ConnectionState _state;
    private bool _isDisposed;

    internal QuicTransportConnection(
        QuicConnection connection,
        TransportId transportId,
        TransportPipeline pipeline,
        QuicStreamType outboundStreamType,
        TransportStreamPipeOptionsContext streamOptions,
        long defaultCloseErrorCode)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(streamOptions);

        _connection = connection;
        _pipeline = pipeline;
        _outboundStreamType = outboundStreamType;
        _streamOptions = streamOptions;
        _defaultCloseErrorCode = defaultCloseErrorCode;
        _contexts = new List<QuicTransportContext>();
        _contextsLock = new Lock();
        _stateLock = new Lock();
        _state = ConnectionState.Idle;
        TransportId = transportId;
    }

    /// <inheritdoc />
    public ConnectionId Id { get; } = ConnectionId.New();

    /// <inheritdoc />
    public TransportId TransportId { get; }

    /// <inheritdoc />
    public TransportProtocol Protocol { get; } = TransportProtocol.Quic;

    /// <inheritdoc />
    public ConnectionState State => _state;

    /// <summary>
    /// Gets the local connection endpoint.
    /// </summary>
    public EndPoint LocalEndPoint => _connection.LocalEndPoint;

    /// <summary>
    /// Gets the remote connection endpoint.
    /// </summary>
    public EndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    internal Action? OnDispose { get; set; }

    /// <inheritdoc />
    public QuicTransportContext OpenInbound()
    {
        return OpenInboundAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask<QuicTransportContext> OpenInboundAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        SetStateOpenIfNeeded();

        QuicStream stream = await _connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
        QuicTransportContext context = CreateContext(stream);

        await _pipeline.ExecuteAsync(this, context, cancellationToken).ConfigureAwait(false);

        return context;
    }

    /// <inheritdoc />
    public QuicTransportContext OpenOutbound()
    {
        return OpenOutboundAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask<QuicTransportContext> OpenOutboundAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        SetStateOpenIfNeeded();

        QuicStream stream = await _connection.OpenOutboundStreamAsync(_outboundStreamType, cancellationToken).ConfigureAwait(false);
        QuicTransportContext context = CreateContext(stream);

        await _pipeline.ExecuteAsync(this, context, cancellationToken).ConfigureAwait(false);

        return context;
    }

    ITransportConnectionContext IMultiplexTransportConnection.OpenInbound()
    {
        return OpenInbound();
    }

    async ValueTask<ITransportConnectionContext> IMultiplexTransportConnection.OpenInboundAsync(CancellationToken cancellationToken)
    {
        return await OpenInboundAsync(cancellationToken).ConfigureAwait(false);
    }

    ITransportConnectionContext IMultiplexTransportConnection.OpenOutbound()
    {
        return OpenOutbound();
    }

    async ValueTask<ITransportConnectionContext> IMultiplexTransportConnection.OpenOutboundAsync(CancellationToken cancellationToken)
    {
        return await OpenOutboundAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Abort()
    {
        AbortAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                return;
            }

            _state = ConnectionState.Aborted;
        }

        try
        {
            await _connection.CloseAsync(_defaultCloseErrorCode, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (QuicException)
        {
        }

        await DisposeAllContextsAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _state = ConnectionState.Closed;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        await AbortAsync().ConfigureAwait(false);

        await _connection.DisposeAsync().ConfigureAwait(false);
        _streamOptions.Dispose();

        OnDispose?.Invoke();
    }

    private QuicTransportContext CreateContext(QuicStream stream)
    {
        var context = new QuicTransportContext(this, stream, _streamOptions.ReaderOptions, _streamOptions.WriterOptions);

        lock (_contextsLock)
        {
            _contexts.Add(context);
        }

        return context;
    }

    private async ValueTask DisposeAllContextsAsync()
    {
        QuicTransportContext[] contexts;

        lock (_contextsLock)
        {
            contexts = _contexts.ToArray();
            _contexts.Clear();
        }

        foreach (QuicTransportContext context in contexts)
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SetStateOpenIfNeeded()
    {
        lock (_stateLock)
        {
            if (_state == ConnectionState.Idle)
            {
                _state = ConnectionState.Open;
                TransportEventSource.Log.TransportConnectionStart(Protocol, TransportId, Id);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(QuicTransportConnection));
    }
}
