using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="Connection"/> over another whose <see cref="Input"/> reads through
/// <see cref="PipeReader.Create(System.IO.Stream, StreamPipeReaderOptions?)"/> over the inner input, as the QUIC
/// driver's request streams read through a <see cref="PipeReader"/> over the <c>QuicStream</c>. Such a reader
/// learns of the peer's FIN only from a read that returns no octets, so a FIN that arrived with the last
/// octets read stays unseen until the next read. Everything else is the inner connection's, including the
/// coded stream aborts of a multiplexed stream end.
/// </summary>
internal sealed class StreamInputConnection : Connection, IMultiplexedStreamAbort
{
    private readonly Connection _inner;

    public StreamInputConnection(Connection inner)
    {
        _inner = inner;
        Input = PipeReader.Create(inner.Input.AsStream());
    }

    /// <inheritdoc />
    public override ConnectionId Id => _inner.Id;

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

    /// <inheritdoc />
    public override PipeReader Input { get; }

    /// <inheritdoc />
    public override PipeWriter Output => _inner.Output;

    /// <inheritdoc />
    public override ConnectionDirection Direction => _inner.Direction;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public override ConnectionState State => _inner.State;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

    /// <inheritdoc />
    public override void Abort(Exception? reason = null) => _inner.Abort(reason);

    /// <inheritdoc />
    public void AbortRead(long errorCode) => (_inner as IMultiplexedStreamAbort)?.AbortRead(errorCode);

    /// <inheritdoc />
    public void AbortWrite(long errorCode) => (_inner as IMultiplexedStreamAbort)?.AbortWrite(errorCode);

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
