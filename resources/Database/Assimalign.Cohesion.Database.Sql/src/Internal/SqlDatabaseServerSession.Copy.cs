using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlDatabaseServerSession
{
    private async Task CopyToAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        if (!_channel!.Capabilities.Contains(SqlProtocol.OutboundStream.Name))
        { throw new ProtocolException(ErrorMessages.CopyStreamRequired); }
        var request = ProtocolCopyToMessage.Decode(frame.Payload.Span);
        var sink = new CopyFrameStream(this);
        try
        {
            if (string.IsNullOrWhiteSpace(request.Statement))
            {
                throw new DatabaseParseException(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.ExecuteStatementCoreAsyncFailure, "CS00003", ErrorMessages.CopyStatementMissing),
                    [new Diagnostic { Code = "CS00003", Message = ErrorMessages.CopyStatementMissing, Severity = DiagnosticSeverity.Error }]);
            }
            long rows = await _databaseSession!.CopyToAsync(request.Statement, sink, cancellationToken).ConfigureAwait(false);
            await sink.StartAsync(cancellationToken).ConfigureAwait(false);
            await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.CopyComplete,
                new ProtocolCopyCompleteMessage(rows, sink.ByteCount).Encode(), cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            // EOF at the acknowledgment frame boundary is a normal peer half-close.
            // CopyToAsync has already released its snapshot and session hold.
            _closeReason = Volatile.Read(ref _abortRequested) == 0
                ? SqlDatabaseEventSource.CloseReason.PeerClosed : SqlDatabaseEventSource.CloseReason.Cancelled;
            return;
        }
        catch (OperationCanceledException) when (_connection.ConnectionClosed.IsCancellationRequested && Volatile.Read(ref _abortRequested) == 0)
        {
            // TCP can publish its receive EOF through ConnectionClosed before the frame
            // reader resumes. That is the same peer close, not a server cancellation.
            _closeReason = SqlDatabaseEventSource.CloseReason.PeerClosed;
            return;
        }
        catch (DatabaseParseException exception)
        {
            await WriteDiagnosticsAsync(PrepareDiagnostics(exception.Diagnostics), cancellationToken).ConfigureAwait(false);
            await WriteErrorAsync(ProtocolErrorCode.ParseFailure, exception.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseException exception)
        {
            await WriteResultFailureAsync(exception, cancellationToken).ConfigureAwait(false);
        }
    }

    [System.Diagnostics.DebuggerDisplay("CopyFrameStream")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(CopyFrameStream.DebugView))]
    private sealed class CopyFrameStream : Stream
    {
        private readonly SqlDatabaseServerSession _owner;
        private bool _started;
        internal CopyFrameStream(SqlDatabaseServerSession owner) { _owner = owner; }
        internal long ByteCount { get; private set; }
        internal async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            if (_started) { return; }
            await _owner.WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.CopyStart, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            while (!buffer.IsEmpty)
            {
                int length = Math.Min(buffer.Length, ProtocolCopyCompleteMessage.MaximumChunkLength);
                await _owner.WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.CopyData, buffer[..length], cancellationToken).ConfigureAwait(false);
                // SQL owns a one-frame delivery window. Transport queues (notably InMemory)
                // may be unbounded; require the consumer's acknowledgment before advancing.
                // No page latch or writer lock is held across this read.
                var acknowledgment = await _owner._reader!.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (acknowledgment is not { } received) { throw new EndOfStreamException(); }
                if (received.Type != (ProtocolMessageType)SqlProtocolMessageType.CopyAcknowledged || !received.Payload.IsEmpty)
                { throw new ProtocolException("CK07005: " + ErrorMessages.CopyAcknowledgmentInvalid); }
                ByteCount = checked(ByteCount + length);
                buffer = buffer[length..];
            }
        }
        private sealed class DebugView
        {
            public DebugView(CopyFrameStream value) { _ = value; }
        }
    }
}
