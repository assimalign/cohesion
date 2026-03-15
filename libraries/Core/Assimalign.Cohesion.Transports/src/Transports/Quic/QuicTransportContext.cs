using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents the QUIC stream context that contains endpoint metadata and the stream pipe.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public sealed class QuicTransportContext : ITransportConnectionContext
{
    private readonly Dictionary<string, object?> _items;

    internal QuicTransportContext(
        QuicTransportConnection connection,
        QuicStream stream,
        StreamPipeReaderOptions readerOptions,
        StreamPipeWriterOptions writerOptions)
    {
        Connection = connection;
        Stream = stream;
        LocalEndPoint = connection.LocalEndPoint;
        RemoteEndPoint = connection.RemoteEndPoint;
        Pipe = new TransportConnectionPipe(stream, readerOptions, writerOptions);
        _items = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the QUIC connection associated with this context.
    /// </summary>
    public QuicTransportConnection Connection { get; }

    internal QuicStream Stream { get; }

    /// <inheritdoc />
    public EndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public EndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public ITransportConnectionPipe Pipe { get; private set; }

    /// <inheritdoc />
    public IDictionary<string, object?> Items => _items;

    /// <summary>
    /// Replaces the active connection pipe with a custom implementation.
    /// </summary>
    /// <param name="pipe">The replacement connection pipe.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipe"/> is <see langword="null"/>.</exception>
    public void SetPipe(ITransportConnectionPipe pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        Pipe = pipe;
    }

    internal ValueTask DisposeAsync()
    {
        return Stream.DisposeAsync();
    }
}