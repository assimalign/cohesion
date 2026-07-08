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
internal sealed class TestConnection : Connection, IStreamIdentifierFeature
{
    private readonly Connection _self;
    private readonly Connection _peer;
    private readonly ConnectionDirection _direction;
    private readonly long _streamId;
    private bool _isAborted;
    private bool _isDisposed;

    public TestConnection(
        byte[]? input = null,
        ConnectionDirection direction = ConnectionDirection.Bidirectional,
        ConnectionCapabilities? capabilities = null,
        EndPoint? localEndPoint = null,
        EndPoint? remoteEndPoint = null,
        bool completeInput = true,
        long streamId = 0)
    {
        _direction = direction;
        _streamId = streamId;

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

        if (completeInput)
        {
            // The peer has sent everything it ever will; the parser observes a finite stream that
            // ends after the preloaded payload (the single-shot shape).
            _peer.Output.Complete();
        }

        // When completeInput is false the peer's writer is left open, so the connection's Input stays
        // live: a read blocks waiting for more bytes rather than seeing end-of-stream. This lets the
        // holder exercise read timeouts / keep-alive deadlines (a stalled peer), where the connection
        // is reclaimed by the server's deadline instead of by end-of-stream.
    }

    public static ConnectionCapabilities DefaultCapabilities => InMemoryConnectionPair.DefaultCapabilities;

    public bool IsAborted => _isAborted;

    public bool IsDisposed => _isDisposed;

    public Exception? AbortReason { get; private set; }

    public override ConnectionId Id => _self.Id;

    /// <summary>
    /// The transport-native stream identifier this double reports through
    /// <see cref="IStreamIdentifierFeature"/>, mirroring the QUIC stream ID the HTTP/3
    /// QPACK decoder keys Section Acknowledgment / Stream Cancellation on.
    /// </summary>
    public long StreamId => _streamId;

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

    /// <summary>
    /// Sends additional bytes from the remote peer to the connection holder after
    /// construction. Used by the flow-control tests to model a sender that resumes
    /// once it has been credited more window via <c>WINDOW_UPDATE</c>. Only valid
    /// when the connection was created with <c>completeInput: false</c>.
    /// </summary>
    public async Task WriteInputAsync(byte[] bytes)
    {
        await _peer.Output.WriteAsync(bytes);
    }

    /// <summary>
    /// Signals that the remote peer has finished sending — the connection holder's
    /// next read past the buffered bytes observes end-of-stream. Pairs with the
    /// <c>completeInput: false</c> constructor option.
    /// </summary>
    public void CompleteInput()
    {
        _peer.Output.Complete();
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
