using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// Presents already-completed in-memory requests through stream-backed pipes, with the transport's
/// known completion either exposed or hidden. Used to measure HTTP/3's default client-fault gate.
/// </summary>
internal sealed class CompletedStreamInputListener : MultiplexedConnectionListener
{
    private readonly InMemoryMultiplexedConnectionListener _inner;
    private readonly bool _publishCompletion;

    public CompletedStreamInputListener(InMemoryMultiplexedConnectionListener inner, bool publishCompletion)
    {
        _inner = inner;
        _publishCompletion = publishCompletion;
    }

    public override EndPoint EndPoint => _inner.EndPoint;

    public override ConnectionCapabilities Capabilities => _inner.Capabilities;

    public override async ValueTask<MultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default)
        => new CompletedInputMultiplexedConnection(await _inner.AcceptAsync(cancellationToken), _publishCompletion);

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class CompletedInputMultiplexedConnection : MultiplexedConnection
    {
        private readonly MultiplexedConnection _inner;
        private readonly bool _publishCompletion;

        public CompletedInputMultiplexedConnection(MultiplexedConnection inner, bool publishCompletion)
        {
            _inner = inner;
            _publishCompletion = publishCompletion;
        }

        public override ConnectionId Id => _inner.Id;

        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

        public override ConnectionCapabilities Capabilities => _inner.Capabilities;

        public override ConnectionState State => _inner.State;

        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

        public override async ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default)
        {
            Connection stream = await _inner.AcceptStreamAsync(cancellationToken);
            if (stream.Direction != ConnectionDirection.Bidirectional)
            {
                return stream;
            }

            // The fixture accepts completed, pre-buffered requests only. Copy before presenting a
            // StreamPipeReader so both cases have identical buffering and differ only in the facet.
            ReadResult read = await stream.Input.ReadAsync(cancellationToken);
            if (!read.IsCompleted)
            {
                throw new InvalidOperationException("The allocation fixture requires a completed request stream.");
            }

            byte[] payload = read.Buffer.ToArray();
            stream.Input.AdvanceTo(read.Buffer.End);
            return new CompletedInputConnection(stream, payload, _publishCompletion);
        }

        public override ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
            => _inner.OpenStreamAsync(direction, cancellationToken);

        public override void Abort(Exception? reason = null) => _inner.Abort(reason);

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class CompletedInputConnection : Connection, IConnectionInputCompletion, IMultiplexedStreamAbort
    {
        private readonly Connection _inner;
        private readonly MemoryStream _payload;
        private readonly bool _publishCompletion;

        public CompletedInputConnection(Connection inner, byte[] payload, bool publishCompletion)
        {
            _inner = inner;
            _payload = new MemoryStream(payload, writable: false);
            _publishCompletion = publishCompletion;
            Input = PipeReader.Create(_payload, new StreamPipeReaderOptions(leaveOpen: true));
        }

        public bool IsInputCompleted => _publishCompletion && _payload.Position == _payload.Length;

        public override ConnectionId Id => _inner.Id;

        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

        public override PipeReader Input { get; }

        public override PipeWriter Output => _inner.Output;

        public override ConnectionDirection Direction => _inner.Direction;

        public override ConnectionCapabilities Capabilities => _inner.Capabilities;

        public override ConnectionState State => _inner.State;

        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

        public override void Abort(Exception? reason = null) => _inner.Abort(reason);

        public void AbortRead(long errorCode) => (_inner as IMultiplexedStreamAbort)?.AbortRead(errorCode);

        public void AbortWrite(long errorCode) => (_inner as IMultiplexedStreamAbort)?.AbortWrite(errorCode);

        public override async ValueTask DisposeAsync()
        {
            await Input.CompleteAsync();
            _payload.Dispose();
            await _inner.DisposeAsync();
        }
    }
}
