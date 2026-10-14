using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// Holds an explicit flush after its bytes have been written, so a test can disconnect during an
/// interim response and choose whether the pipe failure or the cancellation wins the flush.
/// </summary>
internal sealed class InterimFlushConnection : DuplexConnection
{
    private readonly IDuplexConnection _inner;
    private readonly InterimFlushWriter _writer;

    public InterimFlushConnection(IDuplexConnection inner)
    {
        _inner = inner;
        _writer = new InterimFlushWriter(inner.Output);
    }

    public Task FlushEntered => _writer.FlushEntered;

    public void HoldFlush(bool observeCancellation) => _writer.HoldFlush(observeCancellation);

    public void FailFlush(Exception failure) => _writer.FailFlush(failure);

    public override ConnectionId Id => _inner.Id;
    public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;
    public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
    public override PipeReader Input => _inner.Input;
    public override PipeWriter Output => _writer;
    public override ConnectionDirection Direction => _inner.Direction;
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;
    public override ConnectionState State => _inner.State;
    public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;
    public override void Abort(Exception? reason = null) => _inner.Abort(reason);
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class InterimFlushWriter : PipeWriter
    {
        private readonly PipeWriter _inner;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hold;
        private bool _observeCancellation;

        public InterimFlushWriter(PipeWriter inner)
        {
            _inner = inner;
        }

        public Task FlushEntered => _entered.Task;

        public void HoldFlush(bool observeCancellation)
        {
            _observeCancellation = observeCancellation;
            _hold = true;
        }

        public void FailFlush(Exception failure)
        {
            _release.TrySetException(failure);
        }

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(source, cancellationToken);

        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (_hold)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(_observeCancellation ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
            }

            return await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override void Advance(int bytes) => _inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => _inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => _inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);
    }
}
