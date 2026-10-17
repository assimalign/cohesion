using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

// Models a transport that has accepted a flush but has not scheduled its send pump.
// Only output completion releases the queued bytes; disposal discards them.
internal sealed class DeferredOutputListener : DuplexConnectionListener
{
    private readonly InMemoryConnectionListener _inner = new();
    private int _completedOutputs;
    private int _discardedBytes;

    internal int CompletedOutputs => Volatile.Read(ref _completedOutputs);
    internal int DiscardedBytes => Volatile.Read(ref _discardedBytes);
    internal IDuplexConnectionFactory Factory => _inner.CreateFactory();
    public override ListenerId Id => _inner.Id;
    public override EndPoint EndPoint => _inner.EndPoint;
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;
    public override ValueTask BindAsync(CancellationToken cancellationToken = default) => _inner.BindAsync(cancellationToken);
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    protected override async ValueTask<IDuplexConnection> AcceptCoreAsync(CancellationToken cancellationToken = default)
        => new DeferredConnection(await _inner.AcceptAsync(cancellationToken), this);

    private sealed class DeferredConnection : DuplexConnection
    {
        private readonly IDuplexConnection _inner;
        private readonly DeferredWriter _output;

        internal DeferredConnection(IDuplexConnection inner, DeferredOutputListener owner)
        {
            _inner = inner;
            _output = new(inner.Output, owner);
        }

        public override ConnectionId Id => _inner.Id;
        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public override ConnectionCapabilities Capabilities => _inner.Capabilities;
        public override ConnectionState State => _inner.State;
        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;
        public override PipeReader Input => _inner.Input;
        public override PipeWriter Output => _output;
        public override void Abort(Exception? reason = null) => _inner.Abort(reason);

        public override ValueTask DisposeAsync()
        {
            _output.Discard();
            return _inner.DisposeAsync();
        }
    }

    private sealed class DeferredWriter : PipeWriter
    {
        private readonly PipeWriter _inner;
        private readonly DeferredOutputListener _owner;
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private int _completed;

        internal DeferredWriter(PipeWriter inner, DeferredOutputListener owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) => CompleteAsync(exception).GetAwaiter().GetResult();

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new FlushResult(isCanceled: false, isCompleted: false));
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) { return; }
            Interlocked.Increment(ref _owner._completedOutputs);
            await _inner.WriteAsync(_buffer.WrittenMemory);
            _buffer.Clear();
            await _inner.CompleteAsync(exception);
        }

        internal void Discard()
        {
            Interlocked.Add(ref _owner._discardedBytes, _buffer.WrittenCount);
            _buffer.Clear();
        }
    }
}
