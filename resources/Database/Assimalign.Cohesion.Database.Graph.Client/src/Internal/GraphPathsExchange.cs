using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

internal sealed class GraphPathsExchange : DatabaseProtocolExchange<bool>
{
    private readonly GraphProtocolExecuteMessage _request;
    private readonly Action<ProtocolFrame> _consumeFrame;
    private GraphPath? _decodedPath;
    private long _count;
    private bool _pathsComplete;
    // The producer owns all wire I/O under DatabaseConnection's exchange admission. No lock is
    // held across this bounded queue. Cancellation joins the producer before returning its lease;
    // decoded values own their strings/collections and never reference a borrowed frame buffer.
    private readonly Channel<GraphPath> _paths = Channel.CreateBounded<GraphPath>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
    });
    private ExceptionDispatchInfo? _failure;
    private IReadOnlyList<ProtocolDiagnostic> _diagnostics = [];
    private bool _hasDiagnostics;
    private bool _entered;
    private bool _responseComplete;

    internal IReadOnlyList<ProtocolDiagnostic> Diagnostics => _diagnostics;

    internal GraphPathsExchange(string statement, IReadOnlyDictionary<string, object?>? parameters, bool compositeValues = false)
        : base(GraphProtocol.Family, transactionStatus: true)
    {
        _request = GraphRequestEncoder.Create(statement, parameters, compositeValues);
        _consumeFrame = ConsumeFrame;
    }

    internal async Task RunAsync(DatabaseConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.ExecuteAsync(this, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // This worker owns failures from the open exchange seam. Preserve the original
            // diagnostic while still returning an admitted, broken rental exactly once.
            if (_entered && !_responseComplete)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch (Exception releaseFailure)
                {
                    GraphClientEventSource.Log.PathReleaseFailed(releaseFailure);
                }
            }
            _failure = ExceptionDispatchInfo.Capture(exception is DatabaseClientException client
                ? new GraphClientException(client.Code, client.Message, client) : exception);
        }
        finally
        {
            _paths.Writer.TryComplete();
        }
    }

    internal async ValueTask<GraphPath?> ReadPathAsync(Task completion, CancellationToken cancellationToken)
    {
        bool available;
        try
        {
            available = await _paths.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The enumeration token also cancels the producer. Join cleanup before reporting
            // cancellation, and preserve a terminal diagnostic that already won that race.
            await completion.ConfigureAwait(false);
            _failure?.Throw();
            throw;
        }
        if (!available)
        {
            // EOF is observable only after terminal verification and exchange admission release.
            await completion.ConfigureAwait(false);
            _failure?.Throw();
            return null;
        }
        _failure?.Throw();
        _paths.Reader.TryRead(out GraphPath? path);
        return path;
    }

    internal void ClearPending()
    {
        while (_paths.Reader.TryRead(out _)) { }
    }

    protected override async ValueTask<bool> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer,
        CancellationToken cancellationToken)
    {
        _entered = true;
        await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)GraphProtocolMessageType.ExecutePaths, _request.Encode()),
            cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            // Capacity 1 plus waiting before every read prevents a terminal error from
            // overtaking a queued path: it cannot be read until that path is taken.
            // The decode callback returns the payload rental before queue backpressure.
            ValueTask<bool> ready = _paths.Writer.WaitToWriteAsync(cancellationToken);
            if (!ready.IsCompleted) { GraphClientEventSource.Log.PathBackpressureWaited(); }
            if (!await ready.ConfigureAwait(false)) { return false; }
            await ReadFrameAsync(reader, cancellationToken).ConfigureAwait(false);
            if (_pathsComplete) { return true; }
            if (_decodedPath is { } path)
            {
                _decodedPath = null;
                await _paths.Writer.WriteAsync(path, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ConsumeFrame(ProtocolFrame frame)
    {
        if (frame.Type == ProtocolMessageType.Diagnostics)
        {
            if (_hasDiagnostics) { throw new ProtocolException(ErrorMessages.GraphDiagnosticsDuplicate); }
            _hasDiagnostics = true;
            _diagnostics = ProtocolDiagnosticsMessage.Decode(frame.Payload.Span).Entries;
            return;
        }
        if (_hasDiagnostics && frame.Type != ProtocolMessageType.Error
            && frame.Type != (ProtocolMessageType)GraphProtocolMessageType.PathsComplete)
        {
            throw new ProtocolException(ErrorMessages.GraphDiagnosticsOrderInvalid);
        }
        if (frame.Type == ProtocolMessageType.Error)
        {
            var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
            if (_count == 0 && error.Code is ProtocolErrorCode.ParseFailure or ProtocolErrorCode.ExecutionFailure)
            {
                _responseComplete = true;
                MarkResponseComplete();
            }
            throw new DatabaseClientException(error.Code, error.Message, _diagnostics, null);
        }
        if (frame.Type == (ProtocolMessageType)GraphProtocolMessageType.PathsComplete)
        {
            if (GraphProtocolPathsCompleteMessage.Decode(frame.Payload.Span).PathCount != _count)
            {
                throw new ProtocolException("The graph path completion count does not match the received paths.");
            }
            _pathsComplete = true;
            return;
        }
        if (frame.Type != (ProtocolMessageType)GraphProtocolMessageType.Path)
        {
            throw new ProtocolException("Expected a graph path or path completion.");
        }
        long started = Stopwatch.GetTimestamp();
        GraphProtocolPathMessage decoded;
        try { decoded = GraphProtocolPathMessage.Decode(frame.Payload.Span); }
        finally { GraphClientEventSource.Log.PathDecoded(started); }
        _decodedPath = new GraphPath(decoded.Nodes, decoded.Relationships);
        _count = checked(_count + 1);
    }

    private async ValueTask ReadFrameAsync(ProtocolFrameReader reader, CancellationToken cancellationToken)
    {
        try
        {
            if (reader.SupportsBorrowedPayloads)
            {
                if (await reader.ReadBorrowedFrameAsync(_consumeFrame, cancellationToken).ConfigureAwait(false)) { return; }
            }
            else if (await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false) is { } frame)
            {
                ConsumeFrame(frame);
                return;
            }
            throw new ProtocolException(_hasDiagnostics ? ErrorMessages.GraphDiagnosticsCompletionMissing
                : ErrorMessages.PathCompletionMissing);
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException(ErrorMessages.PathResponseReadClosed, exception);
        }
    }
}
