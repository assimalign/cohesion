using Assimalign.Cohesion.Transports.Internal;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public class QuicTransportConnection : ITransportConnection, IPooledStream
{
    private readonly QuicConnection _connection;
    private readonly QuicStream _stream;


    private volatile ConnectionState _state;


    internal PooledStreamStack<QuicTransportConnection> StreamPool;
    private readonly Lock _poolLock = new();
    private readonly Lock _shutdownLock = new();

    internal const int InitialStreamPoolSize = 5;
    internal const int MaxStreamPoolSize = 100;
    internal const long StreamPoolExpirySeconds = 5;

    internal QuicTransportConnection(
        QuicConnection connection,
        int maxReadBufferSize,
        int maxWriteBufferSize,
        bool isServer = true)
    {
        _connection = connection;

        var inputOptions = new PipeOptions(
            null, 
            PipeScheduler.ThreadPool, 
            PipeScheduler.Inline, 
            maxReadBufferSize, 
            maxReadBufferSize / 2,
            useSynchronizationContext: false);

        var outputOptions = new PipeOptions(
            null, 
            PipeScheduler.Inline, 
            PipeScheduler.ThreadPool, 
            maxWriteBufferSize, 
            maxWriteBufferSize / 2, 
            useSynchronizationContext: false);

        var serverPipe = new Pipe(inputOptions);
        var clientPipe = new Pipe(outputOptions);

        if (isServer)
        {
            Pipe = new TransportConnectionPipe(
                serverPipe.Reader,
                clientPipe.Writer);

            this.Output = serverPipe.Writer;
            this.Input = clientPipe.Reader;
        }
        else
        {
            Pipe = new TransportConnectionPipe(
                clientPipe.Reader,
                serverPipe.Writer);

            this.Input = serverPipe.Reader;
            this.Output = clientPipe.Writer;
        }
    }

    public bool IsConnected => throw new NotImplementedException();
    public object? ConnectionData => throw new NotImplementedException();
    public ProtocolType Protocol => ProtocolType.Quic;
    public ConnectionState State => _state;
    public ITransportConnectionPipe Pipe { get; }
    public EndPoint LocalEndPoint => _connection?.LocalEndPoint!;
    public EndPoint RemoteEndPoint => _connection?.RemoteEndPoint!;

    long IPooledStream.PoolExpirationTimestamp => throw new NotImplementedException();

    public readonly PipeWriter Output;
    public readonly PipeReader Input;

    public void Abort()
    {
        
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        
    }

    public void Execute()
    {

        QuicStream quicStream = await _connection.AcceptInboundStreamAsync();

        QuicTransportConnection? connection = null;

        // Only use pool for bidirectional streams. Just a handful of unidirecitonal
        // streams are created for a connection and they live for the lifetime of the connection.
        if (quicStream.CanRead && quicStream.CanWrite)
        {
            lock (_poolLock)
            {
                StreamPool.TryPop(out connection);
            }
        }

        if (connection is null)
        {
            connection = new QuicTransportConnection(
                quicConnection,
                quicStream);

            //connection = new QuicStreamContext(this, _context);
            //context.Initialize(quicStream);
        }
        else
        {
            //context.ResetFeatureCollection();
            //context.ResetItems();
            //context.Initialize(quicStream);

            //QuicLog.StreamReused(_log, context);
        }

        //QuicLog.AcceptedStream(_log, context);

        return connection;

        return new QuicTransportConnection(quicConnection, quicStream);
    }

    void IPooledStream.DisposeCore()
    {
        throw new NotImplementedException();
    }
}
