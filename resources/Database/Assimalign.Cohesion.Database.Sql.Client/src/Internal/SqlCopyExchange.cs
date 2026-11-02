using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client.Internal;

[System.Diagnostics.DebuggerDisplay("SqlCopyExchange")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlCopyExchange.DebugView))]
internal sealed class SqlCopyExchange : DatabaseProtocolExchange<long>
{
    private readonly string _statement;
    private readonly Stream _destination;
    private bool _diagnostics;
    private System.Collections.Generic.IReadOnlyList<ProtocolDiagnostic> _entries = [];
    internal SqlCopyExchange(string statement, Stream destination) : base(SqlProtocol.Family, transactionStatus: true)
    { _statement = statement; _destination = destination; }
    internal bool Entered { get; private set; }
    internal bool Terminal { get; private set; }
    internal bool Started { get; private set; }

    protected override async ValueTask<long> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
    {
        Entered = true;
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)SqlProtocolMessageType.CopyTo,
            new ProtocolCopyToMessage(_statement).Encode()), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        var frame = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
        if (_diagnostics || frame.Type != (ProtocolMessageType)SqlProtocolMessageType.CopyStart || !frame.Payload.IsEmpty)
        { throw new ProtocolException(ErrorMessages.CopyStreamInvalid); }
        Started = true;
        long bytes = 0;
        while (true)
        {
            frame = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
            if (frame.Type == (ProtocolMessageType)SqlProtocolMessageType.CopyComplete)
            {
                var complete = ProtocolCopyCompleteMessage.Decode(frame.Payload.Span);
                if (complete.ByteCount != bytes) { throw new ProtocolException(ErrorMessages.CopyStreamInvalid); }
                Terminal = true;
                return complete.RowCount;
            }
            if (_diagnostics || frame.Type != (ProtocolMessageType)SqlProtocolMessageType.CopyData ||
                frame.Payload.IsEmpty || frame.Payload.Length > ProtocolCopyCompleteMessage.MaximumChunkLength)
            { throw new ProtocolException(ErrorMessages.CopyStreamInvalid); }
            bytes = checked(bytes + frame.Payload.Length);
            // Keep destination failures out of the core's transport-I/O classification. The
            // connection API aborts the incomplete exchange, then rethrows the original failure.
            try { await _destination.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            { throw new SqlCopyDeliveryException(exception); }
            await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)SqlProtocolMessageType.CopyAcknowledged,
                ReadOnlyMemory<byte>.Empty), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<ProtocolFrame> ReadAsync(ProtocolFrameReader reader, CancellationToken cancellationToken)
    {
        var frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException(ErrorMessages.CopyStreamInvalid);
        if (frame.Type == ProtocolMessageType.Diagnostics)
        {
            if (_diagnostics) { throw new ProtocolException(ErrorMessages.CopyStreamInvalid); }
            _diagnostics = true;
            _entries = ProtocolDiagnosticsMessage.Decode(frame.Payload.Span).Entries;
            frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ProtocolException(ErrorMessages.CopyStreamInvalid);
        }
        if (frame.Type == ProtocolMessageType.Error)
        {
            var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
            if (error.Code is ProtocolErrorCode.ParseFailure or ProtocolErrorCode.ExecutionFailure)
            {
                // A late failure can race an outstanding delivery acknowledgment (for
                // example, a server statement timeout). Drain its Error/Ready, then let
                // the API discard the incomplete transfer instead of reusing that stream.
                Terminal = !Started;
                MarkResponseComplete();
            }
            throw new DatabaseClientException(error.Code, error.Message, _entries, null);
        }
        return frame;
    }
    private sealed class DebugView
    {
        public DebugView(SqlCopyExchange value) { _ = value; }
    }
}
