using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An in-memory <see cref="Connection"/> double backed by the shared <see cref="InMemoryConnectionPair"/>
/// driver. The near end is exposed through the standard connection surface, and the far ends are
/// exposed so a test can play the remote peer: bytes written to <see cref="PeerOutput"/> appear on the
/// connection's <see cref="Input"/>, and bytes the connection writes to <see cref="Output"/> can be
/// observed on <see cref="PeerInput"/>. Use <see cref="CreatePair"/> to produce two cross-wired
/// connections that act as each other's remote peer.
/// </summary>
internal sealed class TestConnection : Connection
{
    private readonly Connection _self;
    private readonly Connection _peer;
    private Exception? _abortReason;
    private bool _isAborted;
    private bool _isDisposed;

    public TestConnection(ConnectionCapabilities? capabilities = null)
        : this(CreateEndPointedPair(capabilities))
    {
    }

    private TestConnection((Connection Self, Connection Peer) ends)
    {
        _self = ends.Self;
        _peer = ends.Peer;
    }

    /// <summary>
    /// Gets the default carrier capabilities accepted by the AMQP transports:
    /// a reliable, ordered, single-stream byte stream.
    /// </summary>
    public static ConnectionCapabilities DefaultCapabilities => InMemoryConnectionPair.DefaultCapabilities;

    /// <summary>
    /// Creates two cross-wired connections: bytes written to one side's <see cref="Output"/>
    /// arrive on the other side's <see cref="Input"/>, and vice versa.
    /// </summary>
    public static (TestConnection Left, TestConnection Right) CreatePair(ConnectionCapabilities? capabilities = null)
    {
        (Connection left, Connection right) = CreateEndPointedPair(capabilities);

        // The two ends are already cross-wired by the driver, so each acts as the other's peer.
        return (new TestConnection((left, right)), new TestConnection((right, left)));
    }

    /// <summary>
    /// Gets the far-end writer; bytes written here arrive on the connection's <see cref="Input"/>.
    /// </summary>
    public PipeWriter PeerOutput => _peer.Output;

    /// <summary>
    /// Gets the far-end reader; it observes the bytes the connection wrote to <see cref="Output"/>.
    /// </summary>
    public PipeReader PeerInput => _peer.Input;

    /// <summary>
    /// Gets a value indicating whether any unread bytes are buffered on the far-end reader.
    /// </summary>
    public bool HasBufferedPeerBytes
    {
        get
        {
            if (_peer.Input.TryRead(out ReadResult result))
            {
                bool hasBytes = result.Buffer.Length > 0;
                _peer.Input.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
                return hasBytes;
            }

            return false;
        }
    }

    public bool IsAborted => _isAborted;

    public bool IsDisposed => _isDisposed;

    public Exception? AbortReason => _abortReason;

    public override ConnectionId Id => _self.Id;

    public override EndPoint? LocalEndPoint => _self.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _self.RemoteEndPoint;

    public override PipeReader Input => _self.Input;

    public override PipeWriter Output => _self.Output;

    public override ConnectionCapabilities Capabilities => _self.Capabilities;

    public override ConnectionState State => _self.State;

    public override CancellationToken ConnectionClosed => _self.ConnectionClosed;

    /// <summary>
    /// Plays the remote peer by writing bytes that arrive on the connection's <see cref="Input"/>.
    /// </summary>
    public async Task WritePeerAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        await _peer.Output.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// Completes the far-end writer, signaling end-of-stream on the connection's <see cref="Input"/>.
    /// </summary>
    public void CompletePeerOutput()
    {
        _peer.Output.Complete();
    }

    /// <summary>
    /// Reads everything currently buffered on the far-end reader.
    /// </summary>
    public async Task<byte[]> ReadBufferedPeerBytesAsync(CancellationToken cancellationToken = default)
    {
        ReadResult result = await _peer.Input.ReadAsync(cancellationToken);
        byte[] bytes = result.Buffer.ToArray();
        _peer.Input.AdvanceTo(result.Buffer.End);
        return bytes;
    }

    public override void Abort(Exception? reason = null)
    {
        _isAborted = true;
        _abortReason = reason;
        _self.Abort(reason);
    }

    public override ValueTask DisposeAsync()
    {
        _isDisposed = true;
        return _self.DisposeAsync();
    }

    private static (Connection Self, Connection Peer) CreateEndPointedPair(ConnectionCapabilities? capabilities)
        => InMemoryConnectionPair.Create(
            capabilities ?? DefaultCapabilities,
            clientEndPoint: new IPEndPoint(IPAddress.Loopback, 5672),
            serverEndPoint: new IPEndPoint(IPAddress.Loopback, 45678));
}
