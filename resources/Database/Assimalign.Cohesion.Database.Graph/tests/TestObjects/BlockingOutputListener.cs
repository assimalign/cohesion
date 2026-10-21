using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.Graph.Tests;

// Blocks a selected flush (including writes through AsStream), or output completion.
// Positive flush numbers ignore cancellation; a negative number observes the supplied token.
// Output completion has no cancellation token: only connection abort releases that I/O.
internal sealed class BlockingOutputListener : DuplexConnectionListener
{
    private readonly InMemoryConnectionListener _inner = new();
    private readonly int _blockedFlush;
    private readonly bool _holdOutputAfterAbort;
    private readonly TaskCompletionSource<BlockingConnection> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal BlockingOutputListener(int blockedFlush, bool holdOutputAfterAbort = false)
    {
        _blockedFlush = blockedFlush;
        _holdOutputAfterAbort = holdOutputAfterAbort;
    }

    private readonly TaskCompletionSource<BlockingConnection> _secondAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _accepts;
    internal Task<BlockingConnection> SecondAccepted => _secondAccepted.Task;

    internal Task<BlockingConnection> Accepted => _accepted.Task;
    internal IDuplexConnectionFactory Factory => _inner.CreateFactory();
    public override ListenerId Id => _inner.Id;
    public override EndPoint EndPoint => _inner.EndPoint;
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;
    public override ValueTask BindAsync(CancellationToken cancellationToken = default) => _inner.BindAsync(cancellationToken);
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    protected override async ValueTask<IDuplexConnection> AcceptCoreAsync(CancellationToken cancellationToken = default)
    {
        var connection = new BlockingConnection(await _inner.AcceptAsync(cancellationToken), _blockedFlush, _holdOutputAfterAbort);
        if (Interlocked.Increment(ref _accepts) == 1) { _accepted.TrySetResult(connection); }
        else { _secondAccepted.TrySetResult(connection); }
        return connection;
    }

    internal sealed class BlockingConnection : DuplexConnection
    {
        private readonly IDuplexConnection _inner;
        private readonly BlockingWriter _output;
        private readonly DrainReader _input;
        private readonly bool _holdOutputAfterAbort;
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _aborts;
        private int _disposals;
        private int _pendingOperations;
        private int _flushes;
        private int _completions;

        internal BlockingConnection(IDuplexConnection inner, int blockedFlush, bool holdOutputAfterAbort)
        {
            _inner = inner;
            _holdOutputAfterAbort = holdOutputAfterAbort;
            _output = new BlockingWriter(this, inner.Output, blockedFlush);
            _input = new DrainReader(this, inner.Input);
        }

        internal Task Blocked => _blocked.Task;
        internal Task Aborted => _aborted.Task;
        internal int Aborts => Volatile.Read(ref _aborts);
        internal int Disposals => Volatile.Read(ref _disposals);
        internal int PendingOperations => Volatile.Read(ref _pendingOperations);
        internal int Flushes => Volatile.Read(ref _flushes);
        internal int Completions => Volatile.Read(ref _completions);
        internal bool FlushTokenCanBeCanceled { get; private set; }
        public override ConnectionId Id => _inner.Id;
        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public override ConnectionCapabilities Capabilities => _inner.Capabilities;
        public override ConnectionState State => _inner.State;
        internal bool FailSetup { get; set; }
        public override CancellationToken ConnectionClosed => FailSetup ? throw new InvalidOperationException("Forced rejection setup failure.") : _inner.ConnectionClosed;
        internal bool CloseInputOnDrain { get; set; }
        public override PipeReader Input => _input;
        public override PipeWriter Output => _output;

        public override void Abort(Exception? reason = null)
        {
            Interlocked.Increment(ref _aborts);
            _inner.Abort(reason);
            _aborted.TrySetResult();
            if (!_holdOutputAfterAbort) { ReleaseOutput(); }
        }

        public override async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            await _inner.DisposeAsync();
        }

        internal void ReleaseOutput() => _released.TrySetResult();

        private async Task BlockAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _pendingOperations);
            _blocked.TrySetResult();
            try { await _released.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref _pendingOperations); }
        }

        private sealed class DrainReader : PipeReader
        {
            private readonly BlockingConnection _owner;
            private readonly PipeReader _inner;

            internal DrainReader(BlockingConnection owner, PipeReader inner)
            {
                _owner = owner;
                _inner = inner;
            }

            public override void AdvanceTo(SequencePosition consumed) => _inner.AdvanceTo(consumed);
            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => _inner.AdvanceTo(consumed, examined);
            public override void CancelPendingRead() => _inner.CancelPendingRead();
            public override void Complete(Exception? exception = null) => _inner.Complete(exception);
            public override bool TryRead(out ReadResult result) => _inner.TryRead(out result);
            public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            {
                if (_owner.CloseInputOnDrain && _owner.Completions != 0)
                {
                    throw new ObjectDisposedException("Controlled closed transport");
                }
                return _inner.ReadAsync(cancellationToken);
            }
        }
        private sealed class BlockingWriter : PipeWriter
        {
            private readonly BlockingConnection _owner;
            private readonly PipeWriter _inner;
            private readonly int _blockedFlush;

            internal BlockingWriter(BlockingConnection owner, PipeWriter inner, int blockedFlush)
            {
                _owner = owner;
                _inner = inner;
                _blockedFlush = blockedFlush;
            }

            public override void Advance(int bytes) => _inner.Advance(bytes);
            public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
            public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
            public override void CancelPendingFlush() => _inner.CancelPendingFlush();
            public override void Complete(Exception? exception = null) => _inner.Complete(exception);

            public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            {
                _owner.FlushTokenCanBeCanceled = cancellationToken.CanBeCanceled;
                if (Interlocked.Increment(ref _owner._flushes) == Math.Abs(_blockedFlush))
                {
                    await _owner.BlockAsync(_blockedFlush < 0 ? cancellationToken : CancellationToken.None);
                    throw new ConnectionAbortedException("The controlled transport was aborted during its flush.");
                }
                return await _inner.FlushAsync(cancellationToken);
            }

            public override async ValueTask CompleteAsync(Exception? exception = null)
            {
                Interlocked.Increment(ref _owner._completions);
                if (_blockedFlush == 0) { await _owner.BlockAsync(); }
                await _inner.CompleteAsync(exception);
            }
        }
    }
}
