using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// Observes completed request-stream pipes over the in-memory driver, which does not publish a stream
/// gauge. Its Closing state records output completion.
/// </summary>
internal sealed class CompletionTrackingMultiplexedConnection : MultiplexedConnection
{
    private readonly MultiplexedConnection _inner;
    private readonly List<TrackedConnection> _requests = new();

    public CompletionTrackingMultiplexedConnection(MultiplexedConnection inner)
    {
        _inner = inner;
    }

    public int RequestCount { get { lock (_requests) { return _requests.Count; } } }

    public int DisposedRequestCount { get { lock (_requests) { return _requests.Count(stream => stream.IsDisposed); } } }

    public int CurrentRequests
    {
        get
        {
            lock (_requests)
            {
                return _requests.Count(stream => !stream.InputCompleted || stream.State == ConnectionState.Open);
            }
        }
    }

    protected override IConnectionFeatureCollection InnerFeatures => _inner.Features;

    public override ConnectionId Id => _inner.Id;
    public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;
    public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;
    public override ConnectionState State => _inner.State;
    public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

    public override async ValueTask<MultiplexedStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        MultiplexedStream stream = await _inner.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
        if (stream.Direction != ConnectionDirection.Bidirectional)
        {
            return stream;
        }

        TrackedConnection tracked = new(stream);
        lock (_requests)
        {
            _requests.Add(tracked);
        }

        return tracked;
    }

    public override ValueTask<MultiplexedStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
        => _inner.OpenStreamAsync(direction, cancellationToken);

    public override void Abort(long errorCode, Exception? reason = null) => _inner.Abort(errorCode, reason);

    public override void Abort(Exception? reason = null) => _inner.Abort(reason);
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class TrackedConnection : MultiplexedStream
    {
        private readonly MultiplexedStream _inner;
        private readonly CompletionReader _input;
        private int _disposed;

        public TrackedConnection(MultiplexedStream inner)
        {
            _inner = inner;
            _input = new CompletionReader(inner.Input);
        }

        public override bool IsInputCompleted => _inner.IsInputCompleted;
        public bool InputCompleted => _input.IsCompleted;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        protected override IConnectionFeatureCollection InnerFeatures => _inner.Features;

        public override ConnectionId Id => _inner.Id;
        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public override PipeReader Input => _input;
        public override PipeWriter Output => _inner.Output;
        public override ConnectionDirection Direction => _inner.Direction;
        public override ConnectionCapabilities Capabilities => _inner.Capabilities;
        public override ConnectionState State => _inner.State;
        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;
        public override void Abort(long errorCode, Exception? reason = null) => _inner.Abort(errorCode, reason);

        public override void Abort(Exception? reason = null) => _inner.Abort(reason);
        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _disposed, 1);
        }
    }

    private sealed class CompletionReader : PipeReader
    {
        private readonly PipeReader _inner;
        private int _completed;

        public CompletionReader(PipeReader inner)
        {
            _inner = inner;
        }

        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public override void AdvanceTo(SequencePosition consumed) => _inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => _inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => _inner.CancelPendingRead();
        public override bool TryRead(out ReadResult result) => _inner.TryRead(out result);
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => _inner.ReadAsync(cancellationToken);

        public override void Complete(Exception? exception = null)
        {
            _inner.Complete(exception);
            Volatile.Write(ref _completed, 1);
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            await _inner.CompleteAsync(exception).ConfigureAwait(false);
            Volatile.Write(ref _completed, 1);
        }
    }
}
