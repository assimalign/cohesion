using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// An in-memory <see cref="Connection"/> double backed by the shared
/// <see cref="InMemoryConnectionPair"/> driver. The near end is exposed through the standard
/// connection surface, and the far (peer) end is exposed so a test can play the remote peer: bytes
/// written to <see cref="PeerOutput"/> appear on the connection's <see cref="Input"/>, and bytes the
/// connection writes to <see cref="Output"/> can be observed on <see cref="PeerInput"/>.
/// </summary>
internal sealed class TestConnection : Connection
{
    private readonly Connection _self;
    private readonly Connection _peer;
    private readonly ConnectionDirection _direction;

    public TestConnection(
        ConnectionDirection direction = ConnectionDirection.Bidirectional,
        ConnectionCapabilities? capabilities = null)
    {
        _direction = direction;
        (_self, _peer) = InMemoryConnectionPair.Create(
            capabilities ?? DefaultCapabilities,
            clientEndPoint: new IPEndPoint(IPAddress.Loopback, 11000),
            serverEndPoint: new IPEndPoint(IPAddress.Loopback, 12000));
    }

    public static ConnectionCapabilities DefaultCapabilities => InMemoryConnectionPair.DefaultCapabilities;

    /// <summary>
    /// Gets the far-end writer; bytes written here arrive on the connection's <see cref="Input"/>.
    /// </summary>
    public PipeWriter PeerOutput => _peer.Output;

    /// <summary>
    /// Gets the far-end reader; it observes the bytes the connection wrote to <see cref="Output"/>.
    /// </summary>
    public PipeReader PeerInput => _peer.Input;

    public override ConnectionId Id => _self.Id;

    public override EndPoint? LocalEndPoint => _self.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _self.RemoteEndPoint;

    public override PipeReader Input => _self.Input;

    public override PipeWriter Output => _self.Output;

    public override ConnectionDirection Direction => _direction;

    public override ConnectionCapabilities Capabilities => _self.Capabilities;

    public override ConnectionState State => _self.State;

    public override CancellationToken ConnectionClosed => _self.ConnectionClosed;

    public override void Abort(Exception? reason = null) => _self.Abort(reason);

    public override ValueTask DisposeAsync() => _self.DisposeAsync();
}
