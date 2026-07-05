using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// An in-memory <see cref="Connection"/> double backed by the shared <see cref="InMemoryConnectionPair"/>
/// driver. The supplied payload is preloaded onto the connection's <see cref="Input"/> (as if the
/// remote peer had already sent it, then finished), and everything the holder writes to
/// <see cref="Output"/> can be observed via <see cref="ReadOutputAsync"/>.
/// </summary>
/// <remarks>
/// This preserves the single-shot shape the HTTP protocol tests were written against: a finite input
/// stream plus a captured output. The transport mechanics — cross-wired pipes, the
/// <c>Open → Closing</c> transition when the holder completes <see cref="Output"/>, and close/dispose
/// propagation — now live in the driver rather than in a bespoke pipe-pair implementation.
/// </remarks>
internal sealed class TestConnection : Connection
{
    private readonly Connection _self;
    private readonly Connection _peer;
    private readonly ConnectionDirection _direction;
    private bool _isAborted;
    private bool _isDisposed;

    public TestConnection(
        byte[]? input = null,
        ConnectionDirection direction = ConnectionDirection.Bidirectional,
        ConnectionCapabilities? capabilities = null,
        EndPoint? localEndPoint = null,
        EndPoint? remoteEndPoint = null,
        bool completeInput = true)
    {
        _direction = direction;

        // The pair is created bidirectional (the pipes are always fully functional); the requested
        // direction is only reported, matching the original double, so read-only preloaded streams
        // still capture output if a test inspects it.
        (_self, _peer) = InMemoryConnectionPair.Create(
            capabilities ?? DefaultCapabilities,
            clientEndPoint: localEndPoint ?? new IPEndPoint(IPAddress.Loopback, 8080),
            serverEndPoint: remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 5000));

        if (input is { Length: > 0 })
        {
            // Non-pausing driver pipes let the prime write complete synchronously for any payload size.
            _peer.Output.WriteAsync(input).AsTask().GetAwaiter().GetResult();
        }

        // The peer has sent everything it ever will; the parser observes a finite stream that ends
        // after the preloaded payload.
        _peer.Output.Complete();
    }

    public static ConnectionCapabilities DefaultCapabilities => InMemoryConnectionPair.DefaultCapabilities;

    public bool IsAborted => _isAborted;

    public bool IsDisposed => _isDisposed;

    public Exception? AbortReason { get; private set; }

    public override ConnectionId Id => _self.Id;

    public override EndPoint? LocalEndPoint => _self.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _self.RemoteEndPoint;

    public override PipeReader Input => _self.Input;

    public override PipeWriter Output => _self.Output;

    public override ConnectionDirection Direction => _direction;

    public override ConnectionCapabilities Capabilities => _self.Capabilities;

    public override ConnectionState State => _self.State;

    public override CancellationToken ConnectionClosed => _self.ConnectionClosed;

    /// <summary>
    /// Reads the bytes the connection holder has written to <see cref="Output"/> so far.
    /// </summary>
    public async Task<byte[]> ReadOutputAsync()
    {
        ReadResult result = await _peer.Input.ReadAsync();
        byte[] output = result.Buffer.ToArray();
        _peer.Input.AdvanceTo(result.Buffer.End);
        return output;
    }

    public override void Abort(Exception? reason = null)
    {
        _isAborted = true;
        AbortReason = reason;
        _self.Abort(reason);
    }

    public override ValueTask DisposeAsync()
    {
        _isDisposed = true;
        return _self.DisposeAsync();
    }
}
