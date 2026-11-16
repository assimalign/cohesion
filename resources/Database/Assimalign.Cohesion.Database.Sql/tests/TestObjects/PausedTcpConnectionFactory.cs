using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal sealed class PausedTcpConnectionFactory : DuplexConnectionFactory
{
    private readonly TcpConnectionFactory _inner = new();
    internal PausedConnection? Latest { get; private set; }
    internal int Connects { get; private set; }
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;
    protected override async ValueTask<IDuplexConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken)
    { Connects++; return Latest = new PausedConnection(await _inner.ConnectAsync(endPoint, cancellationToken)); }

    internal sealed class PausedConnection : DuplexConnection
    {
        private readonly IDuplexConnection _inner;
        private readonly PausedReader _reader;
        internal PausedConnection(IDuplexConnection inner) { _inner = inner; _reader = new PausedReader(this, inner.Input); }
        internal IDuplexConnection Inner => _inner;
        internal bool PauseReads { get; set; }
        internal int Aborts { get; private set; }
        public override ConnectionId Id => _inner.Id;
        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public override ConnectionCapabilities Capabilities => _inner.Capabilities;
        public override ConnectionState State => _inner.State;
        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;
        public override PipeReader Input => _reader;
        public override PipeWriter Output => _inner.Output;
        public override void Abort(Exception? reason = null) { Aborts++; _inner.Abort(reason); }
        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class PausedReader : PipeReader
    {
        private readonly PausedConnection _owner;
        private readonly PipeReader _inner;
        internal PausedReader(PausedConnection owner, PipeReader inner) { _owner = owner; _inner = inner; }
        public override void AdvanceTo(SequencePosition consumed) => _inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => _inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => _inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => _inner.Complete(exception);
        public override bool TryRead(out ReadResult result)
        { if (_owner.PauseReads) { result = default; return false; } return _inner.TryRead(out result); }
        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (_owner.PauseReads) { await Task.Delay(Timeout.Infinite, cancellationToken); }
            return await _inner.ReadAsync(cancellationToken);
        }
    }
}
