using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

internal sealed class Http3ConnectionContext : HttpConnectionContext
{
    private readonly IMultiplexedConnection _connection;
    private readonly bool _isSecure;
    private readonly Http3QPackOptions _qpackOptions;
    private readonly Http3PeerSettings _peerSettings = new();
    private readonly CancellationTokenSource _teardownSource = new();
    private readonly QPackDecoderState? _decoderState;
    private IConnection? _controlStream;
    private IConnection? _decoderStream;
    private Task? _peerControlDrainTask;
    private Task? _qpackEncoderDrainTask;
    private bool _controlStreamReceived;
    private bool _qpackEncoderStreamReceived;
    private bool _qpackDecoderStreamReceived;
    // RFC 9114 §5.2 — the number of client-initiated bidirectional request
    // streams this connection has accepted. At teardown the GOAWAY announces
    // the lowest stream ID the server will NOT process; with QUIC's
    // client-bidi numbering (0, 4, 8, …) that boundary is
    // (accepted count) × 4, so every stream already accepted (IDs below the
    // boundary) falls inside "may have been processed" while later streams
    // are rejected. Counted at accept — not at dispatch — so a malformed
    // stream the server touched and dropped is still inside the boundary and
    // the client will not retry a request whose side effects may have run.
    // Mutated with Interlocked from the receive loop; read by the dispose path.
    private int _processedRequestStreamCount;
    // Guards single GOAWAY emission across the receive-loop teardown and the
    // connection dispose path (Interlocked one-shot latch).
    private int _goAwaySent;
    private readonly Http3ConnectionListenerOptions.Http3Limits _limits;
    private readonly IHttpRequestInterceptor[] _requestInterceptors;
    private readonly IHttpResponseInterceptor[] _responseInterceptors;
    // RFC 9218 §7.2 — the effective priority of request streams the peer has
    // re-prioritized via a control-stream PRIORITY_UPDATE. This is the HTTP/3
    // engine's observable priority state; response ordering across streams is
    // otherwise delegated to the QUIC transport (see docs/DESIGN.md). Guarded
    // because the control-stream drain runs on a background task.
    private readonly Dictionary<long, HttpPriority> _requestStreamPriorities = new();
    private readonly object _priorityLock = new();
    private volatile bool _pushPriorityUpdateRejected;

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    public Http3ConnectionContext(
        IMultiplexedConnection connection,
        bool isSecure,
        Http3ConnectionListenerOptions.Http3Limits limits,
        IHttpRequestInterceptor[] requestInterceptors,
        IHttpResponseInterceptor[] responseInterceptors,
        Http3QPackOptions qpackOptions)
    {
        _connection = connection;
        _isSecure = isSecure;
        _limits = limits;
        _requestInterceptors = requestInterceptors;
        _responseInterceptors = responseInterceptors;
        _qpackOptions = qpackOptions;

        // The dynamic table (and its encoder/decoder instruction streams) is
        // opt-in: with QPACK_MAX_TABLE_CAPACITY = 0 the decoder state is never
        // created and the transport stays on the static-only path.
        _decoderState = qpackOptions.DynamicTableEnabled ? new QPackDecoderState(qpackOptions) : null;
    }

    public override EndPoint? LocalEndPoint => _connection.LocalEndPoint;
    public override EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

    /// <summary>
    /// Attempts to read the effective priority recorded for a request stream by a
    /// control-stream PRIORITY_UPDATE frame (RFC 9218 §7.2). Exposes the HTTP/3
    /// engine's observable priority state.
    /// </summary>
    /// <param name="streamId">The prioritized request-stream identifier.</param>
    /// <param name="priority">The recorded priority when present.</param>
    /// <returns><see langword="true"/> if a priority was recorded for the stream; otherwise <see langword="false"/>.</returns>
    internal bool TryGetRequestStreamPriority(long streamId, out HttpPriority priority)
    {
        lock (_priorityLock)
        {
            return _requestStreamPriorities.TryGetValue(streamId, out priority);
        }
    }

    /// <summary>
    /// Whether a push PRIORITY_UPDATE (frame type 0xF0701) has been received and
    /// rejected. The server issues no pushes, so such a frame references a push id
    /// that cannot exist (RFC 9218 §7.2 / H3_ID_ERROR).
    /// </summary>
    internal bool PushPriorityUpdateRejected => _pushPriorityUpdateRejected;

    private void RecordRequestStreamPriority(long streamId, HttpPriority priority)
    {
        lock (_priorityLock)
        {
            _requestStreamPriorities[streamId] = priority;
        }
    }

    /// <summary>
    /// Yields HTTP/3 request contexts for the lifetime of this connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before accepting any inbound stream the server opens its own outbound
    /// unidirectional control stream and sends SETTINGS as the first frame
    /// (RFC 9114 §6.2.1). That control stream stays open for the connection's
    /// lifetime — it is a critical stream — and is torn down connection-first
    /// (see <see cref="ShutdownAsync"/>). SETTINGS emission is best-effort: if
    /// the QUIC connection is already gone, the accept loop below observes the
    /// same failure and terminates.
    /// </para>
    /// <para>
    /// Failure handling is split between two scopes. Per-stream failures —
    /// truncated frames, malformed QPACK, varint overflow, a per-stream
    /// <see cref="IOException"/> — let the loop continue accepting more
    /// inbound streams on the same QUIC connection (HTTP/3 has no
    /// connection-level header table to corrupt, so a bad request on one
    /// stream is harmless to the others). Connection-terminating failures —
    /// the QUIC connection itself going away
    /// (<see cref="QuicException"/> or a contract-level
    /// <see cref="ConnectionException"/>), the multiplexed connection being
    /// disposed, or cancellation — exit the enumerable cleanly so the
    /// listener stays alive for the next peer.
    /// </para>
    /// </remarks>
    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            // RFC 9114 §6.2.1 — each peer MUST open a control stream and send
            // SETTINGS as its first frame. Do this before (and independently
            // of) accepting request streams.
            await SendControlStreamSettingsAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                StreamAcceptOutcome accept = await TryAcceptInboundAsync(cancellationToken).ConfigureAwait(false);

                if (accept.TerminateConnection)
                {
                    yield break;
                }
                if (accept.StreamConnection is null)
                {
                    // Non-terminating accept failure is not expected, but guard
                    // anyway: skip and try the next inbound.
                    continue;
                }

                IConnection streamConnection = accept.StreamConnection;

                // RFC 9114 §6 — a bidirectional stream is a request stream; the
                // peer's unidirectional streams carry a type prefix (control,
                // QPACK encoder/decoder, push) and are demultiplexed separately.
                if (streamConnection.Direction != ConnectionDirection.Bidirectional)
                {
                    if (await TryHandleUnidirectionalStreamAsync(streamConnection, cancellationToken).ConfigureAwait(false))
                    {
                        // A control-stream protocol violation — duplicate control
                        // or QPACK stream, missing/!SETTINGS first frame, or a
                        // client-created push stream — is a connection error
                        // (RFC 9114 §6.2). Terminate the connection.
                        yield break;
                    }

                    continue;
                }

                // RFC 9114 §5.2 — this bidirectional stream is now an accepted
                // request stream; advance the boundary the teardown GOAWAY
                // announces so it falls inside "may have been processed" whether
                // it yields a context or is dropped as malformed below.
                Interlocked.Increment(ref _processedRequestStreamCount);

                RequestReadOutcome outcome = await TryReadRequestAsync(streamConnection, cancellationToken).ConfigureAwait(false);

                if (outcome.TerminateConnection)
                {
                    // A QPACK connection error (RFC 9204 §2.2) corrupts shared
                    // dynamic-table state and cannot be isolated to one stream.
                    yield break;
                }

                if (outcome.Context is not null)
                {
                    yield return outcome.Context;
                }
            }
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens the server's outbound unidirectional control stream and writes the
    /// stream-type prefix (0x00) followed by a SETTINGS frame as its first frame
    /// (RFC 9114 §6.2.1 / §7.2.4). The stream is retained and deliberately left
    /// open — it is a critical stream (RFC 9114 §6.2.1) that must not be
    /// completed, aborted, or FIN'd until connection teardown, or a peer fails
    /// the connection with <c>H3_CLOSED_CRITICAL_STREAM</c>.
    /// </summary>
    /// <remarks>
    /// Best-effort: opening an outbound stream requires a live QUIC connection.
    /// If the connection is already gone the accept loop observes the same
    /// failure and terminates, so a setup failure here is swallowed rather than
    /// surfaced into the consumer's enumeration.
    /// </remarks>
    private async Task SendControlStreamSettingsAsync(CancellationToken cancellationToken)
    {
        if (_controlStream is not null)
        {
            return;
        }

        try
        {
            IConnection controlStream = await _connection
                .OpenStreamAsync(ConnectionDirection.WriteOnly, cancellationToken)
                .ConfigureAwait(false);
            _controlStream = controlStream;

            // Write the control-stream preamble directly to the outbound pipe.
            // PipeWriter.WriteAsync flushes, so the SETTINGS frame reaches the
            // transport as the stream's first bytes. The output is not completed
            // — the critical stream stays open for the connection lifetime.
            byte[] preamble = BuildControlStreamPreamble(_qpackOptions);
            await controlStream.Output.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);

            // When the dynamic table is enabled the server also opens its own
            // QPACK decoder stream (RFC 9204 §4.2) so it can send decoder
            // instructions (Insert Count Increment, and — with a stream ID —
            // Section Acknowledgment). Like the control stream it is a critical
            // stream: its type prefix is written and it is left open.
            if (_decoderState is not null)
            {
                IConnection decoderStream = await _connection
                    .OpenStreamAsync(ConnectionDirection.WriteOnly, cancellationToken)
                    .ConfigureAwait(false);
                _decoderStream = decoderStream;

                byte[] decoderPrefix = BuildStreamTypePrefix(Http3StreamType.QPackDecoder);
                await decoderStream.Output.WriteAsync(decoderPrefix, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (QuicException)
        {
        }
        catch (ConnectionException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
        }
    }

    private static byte[] BuildStreamTypePrefix(long streamType)
    {
        using MemoryStream buffer = new();
        QuicVariableLengthInteger.Write(buffer, streamType);
        return buffer.ToArray();
    }

    /// <summary>
    /// Builds the bytes for the server control stream's opening: the RFC 9114
    /// §6.2 unidirectional stream-type prefix (0x00 = control) followed by a
    /// SETTINGS frame (type 0x04) carrying the server's advertised settings.
    /// </summary>
    private static byte[] BuildControlStreamPreamble(Http3QPackOptions qpackOptions)
    {
        using MemoryStream buffer = new();

        // RFC 9114 §6.2 — the control stream is identified by a stream-type
        // varint of 0x00 as its first bytes.
        QuicVariableLengthInteger.Write(buffer, Http3StreamType.Control);

        // RFC 9114 §6.2.1 / §7.2.4 — the first frame MUST be SETTINGS.
        byte[] settings = Http3LocalSettings.EncodePayload(qpackOptions);
        QuicVariableLengthInteger.Write(buffer, (long)Http3FrameType.Settings);
        QuicVariableLengthInteger.Write(buffer, settings.Length);
        buffer.Write(settings, 0, settings.Length);

        return buffer.ToArray();
    }

    /// <summary>
    /// Tears the connection context down when the receive enumerable completes:
    /// signals the inbound control-stream drain to stop and waits for it to
    /// finish. The outbound control stream is deliberately not completed here —
    /// teardown stays connection-first, so the multiplexed connection's own
    /// disposal (via <see cref="Http3Connection.DisposeAsync"/>) closes the QUIC
    /// connection before releasing its streams, and a peer never observes
    /// <c>H3_CLOSED_CRITICAL_STREAM</c> ahead of <c>CONNECTION_CLOSE</c>.
    /// </summary>
    private async Task ShutdownAsync()
    {
        if (!_teardownSource.IsCancellationRequested)
        {
            _teardownSource.Cancel();
        }

        if (_peerControlDrainTask is not null)
        {
            // The drain swallows its own failures, so awaiting it here cannot
            // throw; it just lets the background loop unwind before teardown
            // completes.
            await _peerControlDrainTask.ConfigureAwait(false);
        }

        if (_qpackEncoderDrainTask is not null)
        {
            // Same contract as the control-stream drain: it absorbs its own
            // failures, so awaiting it just lets the background loop unwind.
            await _qpackEncoderDrainTask.ConfigureAwait(false);
        }

        _teardownSource.Dispose();
    }

    /// <summary>
    /// Emits the server's <c>GOAWAY</c> frame on the outbound control stream
    /// (RFC 9114 §5.2 / §7.2.6) to announce graceful shutdown before the QUIC
    /// connection is closed. The announced identifier is the lowest
    /// client-initiated bidirectional stream ID the server will not process —
    /// derived from the count of request streams already accepted using QUIC's
    /// client-bidi numbering (ID = 4 × <c>n</c>) — so requests at or below the
    /// highest accepted stream may finish while later streams are rejected.
    /// </summary>
    /// <param name="cancellationToken">Cancels the control-stream write.</param>
    /// <returns>A task that completes once the GOAWAY has been written (or skipped).</returns>
    /// <remarks>
    /// <para>
    /// One-shot: repeated calls after the first are no-ops. When the receive loop
    /// never ran (no control stream was opened) there is nothing to announce and
    /// the call returns without writing.
    /// </para>
    /// <para>
    /// Best-effort, like the SETTINGS emission: writing GOAWAY requires a live
    /// control stream, so a wire-level or QUIC failure here is swallowed — the
    /// connection is tearing down regardless, and the QUIC <c>CONNECTION_CLOSE</c>
    /// that follows conveys the shutdown even if the frame did not land.
    /// </para>
    /// </remarks>
    internal async Task SendGoAwayAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _goAwaySent, 1) == 1)
        {
            return;
        }

        IConnection? controlStream = _controlStream;
        if (controlStream is null)
        {
            // The server never opened its control stream (the receive loop did
            // not run), so it advertised no SETTINGS and has no critical stream
            // to carry GOAWAY. The QUIC close alone tears the connection down.
            return;
        }

        try
        {
            // RFC 9114 §5.2 — streams with an ID below the announced value may
            // have been processed. The lowest unprocessed client-initiated
            // bidirectional stream is (accepted count) × 4.
            long goAwayStreamId = (long)Volatile.Read(ref _processedRequestStreamCount) * 4L;
            byte[] frame = Http3GoAwayFrame.Encode(goAwayStreamId);
            await controlStream.Output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (QuicException)
        {
        }
        catch (ConnectionException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
        }
    }

    /// <summary>
    /// Demultiplexes a peer-initiated unidirectional stream by its RFC 9114
    /// §6.2 stream-type prefix. Returns <see langword="true"/> when the stream
    /// is a connection-level protocol violation that must terminate the
    /// connection (duplicate control / QPACK stream, missing or non-SETTINGS
    /// first control frame, or a client-created push stream); otherwise
    /// <see langword="false"/>.
    /// </summary>
    private async Task<bool> TryHandleUnidirectionalStreamAsync(IConnection streamConnection, CancellationToken cancellationToken)
    {
        // RFC 9114 §6.2 — read directly off the stream connection's PipeReader
        // rather than the Stream adapter. Unidirectional streams are processed
        // incrementally (a varint stream-type prefix, then type-specific
        // frames), which the buffered ReadOnlySequence model expresses directly
        // without the adapter's read-size quirks.
        PipeReader reader = streamConnection.Input;

        long? streamType;
        try
        {
            streamType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // The stream type could not be read — RFC 9114 §6.2 permits
            // abandoning an unparseable unidirectional stream without
            // affecting the connection.
            return false;
        }

        if (streamType is null)
        {
            return false;
        }

        switch (streamType.Value)
        {
            case Http3StreamType.Control:
                return await TryHandleControlStreamAsync(reader, cancellationToken).ConfigureAwait(false);

            case Http3StreamType.QPackEncoder:
                // RFC 9204 §4.2 — at most one encoder stream. With the dynamic
                // table disabled (QPACK_MAX_TABLE_CAPACITY = 0) it carries no
                // instructions to process, so accepting it is sufficient. With the
                // table enabled, drain it in the background so its Set Capacity /
                // Insert / Duplicate instructions populate the dynamic table while
                // the accept loop keeps serving requests.
                if (_qpackEncoderStreamReceived)
                {
                    return true;
                }

                _qpackEncoderStreamReceived = true;

                if (_decoderState is not null)
                {
                    _qpackEncoderDrainTask = DrainQPackEncoderStreamAsync(reader, cancellationToken);
                }

                return false;

            case Http3StreamType.QPackDecoder:
                if (_qpackDecoderStreamReceived)
                {
                    return true;
                }

                _qpackDecoderStreamReceived = true;
                return false;

            case Http3StreamType.Push:
                // RFC 9114 §6.2.2 — a client MUST NOT create a push stream;
                // treat it as H3_STREAM_CREATION_ERROR.
                return true;

            default:
                // RFC 9114 §6.2 — unknown unidirectional stream types are not
                // an error; the recipient may abandon them.
                return false;
        }
    }

    /// <summary>
    /// Reads and applies the peer's control stream. Enforces a single control
    /// stream and that its first frame is SETTINGS (RFC 9114 §6.2.1 / §7.2.4),
    /// then hands the stream to a background drain for the connection lifetime.
    /// Returns <see langword="true"/> on a protocol violation.
    /// </summary>
    private async Task<bool> TryHandleControlStreamAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        if (_controlStreamReceived)
        {
            // RFC 9114 §6.2.1 — only one control stream per peer.
            return true;
        }

        _controlStreamReceived = true;

        try
        {
            // RFC 9114 §6.2.1 / §7.2.4 — the first frame on the control stream
            // MUST be SETTINGS. Read and apply it synchronously so a missing or
            // non-SETTINGS opening frame terminates the connection inline.
            long? frameType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
            long? frameLength = frameType is null
                ? null
                : await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);

            if (frameType is null || frameLength is null || frameType.Value != (long)Http3FrameType.Settings)
            {
                // Missing or non-SETTINGS first frame — H3_MISSING_SETTINGS.
                return true;
            }

            byte[] payload = await ReadExactAsync(reader, checked((int)frameLength.Value), cancellationToken).ConfigureAwait(false);
            ApplySettings(payload);
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // The control stream is critical; a read/parse failure on the
            // mandatory SETTINGS frame is a connection error
            // (H3_FRAME_ERROR / H3_CLOSED_CRITICAL_STREAM).
            return true;
        }

        // RFC 9114 §6.2.1 — the control stream stays open for the connection
        // lifetime. Drain any post-SETTINGS frames on a background task so they
        // cannot accumulate unread in the pipe and so the accept loop is never
        // blocked waiting on the long-lived control stream.
        _peerControlDrainTask = DrainPeerControlStreamAsync(reader, cancellationToken);
        return false;
    }

    /// <summary>
    /// Processes post-SETTINGS frames from the peer's control stream for the
    /// connection lifetime (RFC 9114 §7.2). RFC 9218 §7.2 PRIORITY_UPDATE frames
    /// are parsed and applied: a request-stream update (0xF0700) records the
    /// referenced stream's effective priority; a push update (0xF0701) is rejected
    /// (the server issues no pushes) and stops the drain. GOAWAY (§7.2.6) and
    /// MAX_PUSH_ID (§7.2.7) are read but inert in this subset, and every other
    /// frame is discarded. Processing prevents unread control frames from
    /// accumulating in the pipe. The loop stops on end-of-stream, connection
    /// teardown, or a per-stream parse failure, and never throws into the receive
    /// loop.
    /// </summary>
    private async Task DrainPeerControlStreamAsync(PipeReader reader, CancellationToken receiveToken)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(receiveToken, _teardownSource.Token);
        CancellationToken cancellationToken = linked.Token;

        try
        {
            while (true)
            {
                long? frameType = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
                if (frameType is null)
                {
                    // Clean end of the peer control stream — nothing more to drain.
                    break;
                }

                long? frameLength = await ReadVarintAsync(reader, cancellationToken).ConfigureAwait(false);
                if (frameLength is null)
                {
                    break;
                }

                int length = checked((int)frameLength.Value);

                if (frameType.Value == (long)Http3FrameType.PriorityUpdateRequest)
                {
                    // RFC 9218 §7.2 — read the payload (Prioritized Element ID +
                    // Priority Field Value) and apply it to the referenced stream.
                    byte[] priorityPayload = await ReadExactAsync(reader, length, cancellationToken).ConfigureAwait(false);
                    if (Http3PriorityUpdate.TryParse(priorityPayload, out long prioritizedStreamId, out HttpPriority priority))
                    {
                        RecordRequestStreamPriority(prioritizedStreamId, priority);
                    }

                    continue;
                }

                if (frameType.Value == (long)Http3FrameType.PriorityUpdatePush)
                {
                    // RFC 9218 §7.2 — the server advertises no push capacity, so a
                    // push PRIORITY_UPDATE references a push id that cannot exist.
                    // Reject it consistently with the server-push de-scope: record
                    // the rejection and stop draining so connection teardown closes
                    // the QUIC connection. (Strict HTTP/3 would signal H3_ID_ERROR;
                    // this drain keeps its parse-and-discard posture — see docs/DESIGN.md.)
                    await SkipAsync(reader, length, cancellationToken).ConfigureAwait(false);
                    _pushPriorityUpdateRejected = true;
                    break;
                }

                await SkipAsync(reader, length, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Connection teardown, or the receive token firing — stop draining.
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // A malformed post-SETTINGS control frame. Strict HTTP/3 would treat
            // this as a connection error; in this parse-and-discard subset the
            // drain stops and connection teardown closes the QUIC connection.
        }
    }

    /// <summary>
    /// Drains the peer's QPACK encoder stream for the connection lifetime,
    /// applying its Set Dynamic Table Capacity / Insert / Duplicate instructions
    /// (RFC 9204 §4.3) to the shared decoder dynamic table and emitting an Insert
    /// Count Increment (§4.4.3) on the server's decoder stream for each batch of
    /// applied insertions. A malformed instruction or table violation is a
    /// connection error (§2.2): it aborts the connection so the accept loop
    /// observes the failure and terminates. Runs only when the dynamic table is
    /// enabled.
    /// </summary>
    private async Task DrainQPackEncoderStreamAsync(PipeReader reader, CancellationToken receiveToken)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(receiveToken, _teardownSource.Token);
        CancellationToken cancellationToken = linked.Token;

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                int consumed = 0;
                int insertions = 0;

                if (!buffer.IsEmpty)
                {
                    // Copy the unconsumed span so the parser can work over a
                    // contiguous buffer; instruction volumes are small.
                    byte[] bytes = buffer.ToArray();
                    consumed = _decoderState!.ApplyEncoderInstructions(bytes, out insertions);
                }

                if (insertions > 0)
                {
                    await SendDecoderInstructionAsync(
                        QPackDecoderInstructionEncoder.InsertCountIncrement(insertions),
                        cancellationToken).ConfigureAwait(false);
                }

                // Consume the complete instructions; keep the trailing partial
                // (examined = end) so the next read waits for more bytes.
                reader.AdvanceTo(buffer.GetPosition(consumed), buffer.End);

                if (result.IsCompleted)
                {
                    if (consumed < buffer.Length)
                    {
                        throw new QPackException(
                            Http3ErrorCode.QPackEncoderStreamError,
                            "The QPACK encoder stream ended in the middle of an instruction.");
                    }

                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Connection teardown or the receive token firing — stop draining.
        }
        catch (QPackException ex)
        {
            // A connection-level QPACK error: signal teardown so any request
            // stream currently blocked on pending insertions unblocks, then abort
            // so the accept loop's next AcceptStreamAsync observes the failure and
            // terminates the connection.
            if (!_teardownSource.IsCancellationRequested)
            {
                _teardownSource.Cancel();
            }

            _connection.Abort(ex);
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            // A wire failure on the encoder stream; teardown closes the connection.
        }
    }

    /// <summary>
    /// Writes a QPACK decoder-stream instruction (Insert Count Increment, or —
    /// with a stream ID — Section Acknowledgment / Stream Cancellation) to the
    /// server's outbound decoder stream. Called only from the single encoder-drain
    /// task, so the writer is never contended.
    /// </summary>
    private async Task SendDecoderInstructionAsync(byte[] instruction, CancellationToken cancellationToken)
    {
        if (_decoderStream is null)
        {
            return;
        }

        await _decoderStream.Output.WriteAsync(instruction, cancellationToken).ConfigureAwait(false);
    }

    private void ApplySettings(byte[] payload)
    {
        int index = 0;
        while (index < payload.Length)
        {
            long identifier = QuicVariableLengthInteger.Decode(payload, ref index);
            long value = QuicVariableLengthInteger.Decode(payload, ref index);
            _peerSettings.Set(identifier, value);
        }
    }

    /// <summary>
    /// Reads a single QUIC variable-length integer off the pipe, buffering
    /// across reads until a complete integer is available. Returns
    /// <see langword="null"/> when the stream ends before any byte arrives.
    /// </summary>
    private static async Task<long?> ReadVarintAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (QuicVariableLengthInteger.TryDecode(buffer, out long value, out SequencePosition consumed))
            {
                reader.AdvanceTo(consumed);
                return value;
            }

            if (result.IsCompleted)
            {
                if (buffer.IsEmpty)
                {
                    // Clean end of stream before the next integer began.
                    reader.AdvanceTo(buffer.End);
                    return null;
                }

                // Bytes remain but cannot form a complete integer.
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("The QUIC variable-length integer was incomplete.");
            }

            // Not enough buffered yet; mark everything examined so the next
            // read waits for more bytes rather than returning the same span.
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes off the pipe, buffering
    /// across reads. Throws <see cref="EndOfStreamException"/> when the stream
    /// ends first.
    /// </summary>
    private static async Task<byte[]> ReadExactAsync(PipeReader reader, int length, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= length)
            {
                ReadOnlySequence<byte> slice = buffer.Slice(0, length);
                byte[] payload = slice.ToArray();
                reader.AdvanceTo(slice.End);
                return payload;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("The HTTP/3 control SETTINGS frame was truncated.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Reads and discards exactly <paramref name="length"/> bytes off the pipe,
    /// buffering across reads. Used to drain the payload of a post-SETTINGS
    /// control frame without allocating a buffer for bytes that are thrown away.
    /// Throws <see cref="EndOfStreamException"/> when the stream ends first.
    /// </summary>
    private static async Task SkipAsync(PipeReader reader, int length, CancellationToken cancellationToken)
    {
        int remaining = length;

        while (remaining > 0)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= remaining)
            {
                reader.AdvanceTo(buffer.GetPosition(remaining));
                return;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("An HTTP/3 control frame was truncated.");
            }

            remaining -= (int)buffer.Length;
            reader.AdvanceTo(buffer.End);
        }
    }

    /// <summary>
    /// Accepts the next inbound QUIC stream on this multiplexed connection.
    /// QUIC-level failures (peer aborted the connection, the multiplexed
    /// connection was disposed) and cancellation signal connection
    /// termination so the receive loop exits without throwing into the
    /// caller's <c>await foreach</c>.
    /// </summary>
    private async Task<StreamAcceptOutcome> TryAcceptInboundAsync(CancellationToken cancellationToken)
    {
        try
        {
            IConnection streamConnection = await _connection.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
            return new StreamAcceptOutcome(streamConnection, terminate: false);
        }
        catch (OperationCanceledException)
        {
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (QuicException)
        {
            // The QUIC connection itself is gone — peer aborted, idle
            // timeout, or a transport-level error. No more streams will
            // arrive on this connection.
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (ConnectionException)
        {
            // Contract-level abort/reset surfaced by the multiplexed
            // connection (ConnectionAbortedException / ConnectionResetException).
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (ObjectDisposedException)
        {
            // The underlying multiplexed connection has been disposed
            // (cooperative shutdown raced this accept).
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
            return new StreamAcceptOutcome(streamConnection: null, terminate: true);
        }
    }

    /// <summary>
    /// Reads a single request off the supplied QUIC stream. Any per-stream
    /// failure — truncated frames, malformed QPACK literal, overflow in a
    /// QUIC varint, an I/O error on this stream alone — is absorbed and
    /// returns <see langword="null"/>, signalling the caller to drop this
    /// stream and keep accepting more on the same QUIC connection.
    /// </summary>
    private async Task<RequestReadOutcome> TryReadRequestAsync(IConnection streamConnection, CancellationToken cancellationToken)
    {
        try
        {
            return new RequestReadOutcome(await ReadRequestAsync(streamConnection, cancellationToken).ConfigureAwait(false), terminate: false);
        }
        catch (QPackException)
        {
            // A QPACK decompression / instruction failure is a connection error
            // (RFC 9204 §2.2): the shared dynamic table cannot be trusted, so the
            // connection terminates rather than dropping just this stream.
            return new RequestReadOutcome(context: null, terminate: true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation during a per-stream read is treated as
            // per-stream — the outer loop check on cancellationToken will
            // break out at the top of the next iteration if the cancel
            // applies to the whole connection.
            return new RequestReadOutcome(context: null, terminate: false);
        }
        catch (Exception ex) when (IsPerStreamFailure(ex))
        {
            return new RequestReadOutcome(context: null, terminate: false);
        }
    }

    private readonly struct RequestReadOutcome
    {
        public RequestReadOutcome(Http3Context? context, bool terminate)
        {
            Context = context;
            TerminateConnection = terminate;
        }

        public Http3Context? Context { get; }
        public bool TerminateConnection { get; }
    }

    /// <summary>
    /// Wire-level transport failures that can surface from
    /// <c>AcceptStreamAsync</c>: low-level <see cref="IOException"/>,
    /// <see cref="System.Net.Sockets.SocketException"/>, and unexpected
    /// end-of-stream during the accept handshake. The QUIC connection is
    /// no longer usable when these fire.
    /// </summary>
    private static bool IsWireLevelFailure(Exception exception)
    {
        return exception is EndOfStreamException
            or IOException
            or System.Net.Sockets.SocketException;
    }

    /// <summary>
    /// Per-stream parse failures from the HTTP/3 frame reader and the
    /// QPACK literal decoder. These do not invalidate the QUIC connection
    /// — the offending stream is dropped and the receive loop keeps
    /// accepting subsequent streams.
    /// </summary>
    private static bool IsPerStreamFailure(Exception exception)
    {
        return exception is EndOfStreamException
            or InvalidDataException
            or NotSupportedException
            or OverflowException
            or ArgumentOutOfRangeException
            or IndexOutOfRangeException
            or IOException;
    }

    private readonly struct StreamAcceptOutcome
    {
        public StreamAcceptOutcome(IConnection? streamConnection, bool terminate)
        {
            StreamConnection = streamConnection;
            TerminateConnection = terminate;
        }

        public IConnection? StreamConnection { get; }
        public bool TerminateConnection { get; }
    }

    public override async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http3Context http3Context)
        {
            throw new InvalidOperationException("The supplied context does not belong to an HTTP/3 connection.");
        }

        // The exchange was aborted (IHttpExchangeControl.Abort / IHttpContext.Cancel — the
        // directive is Abort). RFC 9114 §4.1 — reset the request stream instead of writing a
        // response; the QUIC connection and its other streams are unaffected.
        if (http3Context.CancelRequested)
        {
            http3Context.StreamConnection.Abort();
            return;
        }

        // If a response feature streamed to the raw sink, the HEADERS and DATA frames are already
        // on the wire (the BeforeResponseHead hooks fired at the sink's head commit); finalize
        // instead of writing a buffered response.
        if (http3Context.ResponseBodySink is { HasStarted: true } sink)
        {
            await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await http3Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // The final response head is about to be committed on the buffered path — the last
        // mutation point. Fire the BeforeResponseHead lifecycle hooks, then re-read the directive
        // so a hook that aborted the exchange resets the stream instead of writing the head.
        await http3Context.InvokeBeforeResponseHeadAsync(cancellationToken).ConfigureAwait(false);

        if (http3Context.CancelRequested)
        {
            http3Context.StreamConnection.Abort();
            return;
        }

        // A hook may itself have started the response through the raw sink (its HEADERS frame is
        // then already on the wire) — finalize that response rather than writing a second one.
        if (http3Context.ResponseBodySink is { HasStarted: true } hookStartedSink)
        {
            await hookStartedSink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await http3Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // RFC 9110 §15.2 — a 1xx status is never a valid final response status. Interim responses go
        // through the IHttpExchangeControl interim writes; HTTP/3 has no 101 (RFC 9114 §4.2), so the
        // rejection is unconditional.
        HttpInterimResponseRules.EnsureFinalStatusCode(http3Context.Response.StatusCode);

        // Commit point: from here the final response is on the wire, so the exchange control's
        // probes must report the response as started (no more interim writes).
        http3Context.MarkFinalResponseStarted();

        Stream stream = http3Context.StreamConnection.AsStream();
        byte[] bodyBytes = await ReadBodyAsync(http3Context.Response.Body, cancellationToken).ConfigureAwait(false);
        byte[] headerBlock = Http3HeaderCodec.EncodeResponseHeaders(http3Context, bodyBytes);

        await WriteFrameAsync(stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);

        if (bodyBytes.Length > 0)
        {
            await WriteFrameAsync(stream, Http3FrameType.Data, bodyBytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await http3Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an interim (<c>1xx</c>) response as an additional QPACK-encoded HEADERS frame on the
    /// exchange's request stream, ahead of the final HEADERS frame (RFC 9114 §4.1). The field section
    /// carries the <c>1xx</c> <c>:status</c> and the supplied fields with no <c>Content-Length</c>. The
    /// request stream is single-writer for the response direction, so the interim frame simply
    /// precedes the final frames on the same QUIC stream; QUIC flow control provides backpressure.
    /// </summary>
    /// <param name="http3Context">The exchange whose interim response is emitted.</param>
    /// <param name="statusCode">The interim status code (validated by the caller to be 1xx, not 101).</param>
    /// <param name="headers">The interim response fields, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    internal async Task WriteInterimResponseAsync(
        Http3Context http3Context,
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers,
        CancellationToken cancellationToken)
    {
        Stream stream = http3Context.StreamConnection.AsStream();
        byte[] headerBlock = Http3HeaderCodec.EncodeInterimResponseHeaders(statusCode, headers);

        await WriteFrameAsync(stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Http3Context?> ReadRequestAsync(IConnection streamConnection, CancellationToken cancellationToken)
    {
        Stream stream = streamConnection.AsStream();
        using MemoryStream requestBuffer = new();
        await stream.CopyToAsync(requestBuffer, cancellationToken).ConfigureAwait(false);

        byte[] requestBytes = requestBuffer.ToArray();
        byte[]? headerBlock = null;
        using MemoryStream body = new();
        int index = 0;

        while (index < requestBytes.Length)
        {
            long frameType = QuicVariableLengthInteger.Decode(requestBytes, ref index);
            long frameLength = QuicVariableLengthInteger.Decode(requestBytes, ref index);
            byte[] payload = requestBytes.AsSpan(index, checked((int)frameLength)).ToArray();
            index += checked((int)frameLength);

            switch ((Http3FrameType)frameType)
            {
                case Http3FrameType.Headers:
                    headerBlock = payload;
                    break;
                case Http3FrameType.Data:
                    await body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (headerBlock is null)
        {
            return null;
        }

        byte[] bodyBytes = body.ToArray();
        HttpScheme fallbackScheme = _isSecure ? HttpScheme.Https : HttpScheme.Http;
        string? extendedConnectProtocol;
        Http3Request request;

        if (_decoderState is not null)
        {
            // Dynamic QPACK path: resolve against the connection dynamic table,
            // blocking (within the blocked-stream budget) until the referenced
            // insertions arrive (RFC 9204 §2.1.2), then apply the shared HTTP/3
            // field-section validation. The wait is linked to connection teardown
            // so an encoder-stream abort or graceful stop releases a blocked
            // stream instead of hanging it.
            using CancellationTokenSource decodeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _teardownSource.Token);
            QPackDecodeResult decode = await _decoderState.DecodeRequestAsync(headerBlock, decodeCancellation.Token).ConfigureAwait(false);
            request = Http3HeaderCodec.BuildRequest(decode.Fields, fallbackScheme, bodyBytes, out extendedConnectProtocol);

            // A field section that referenced the dynamic table owes a Section
            // Acknowledgment on the decoder stream (RFC 9204 §4.4.1). The
            // instruction is keyed on the QUIC request stream ID, which the
            // connection abstraction does not yet surface, so live emission is
            // deferred (see docs/DESIGN.md and the filed follow-up). Insert Count
            // Increment — which needs no stream ID — is emitted by the encoder
            // drain and keeps the peer's Known Received Count advancing.
            _ = decode.ReferencedDynamicTable;
        }
        else
        {
            request = Http3HeaderCodec.DecodeRequestHeaders(headerBlock, fallbackScheme, bodyBytes, out extendedConnectProtocol);
        }

        Http3Response response = new();
        HttpConnectionInfo connectionInfo = new(streamConnection.LocalEndPoint, streamConnection.RemoteEndPoint);

        // Request-parse interceptor phase — the HTTP/3 analogue of the HTTP/1.1 invocation point,
        // run as the request head is assembled. RFC 9110 §9.3.6 — a CONNECT's post-head octets are
        // tunnel traffic, so its body hooks are skipped (head hooks still run). The hook-populated
        // feature collection flows into the exchange through the Http3Context features parameter;
        // zero interceptors keeps the pre-seam fast path.
        bool isConnect = request.Method == HttpMethod.Connect;
        HttpFeatureCollection? features;
        try
        {
            features = await HttpRequestInterceptorPipeline.InvokeAsync(
                _requestInterceptors,
                HttpVersion.Http30,
                request,
                connectionInfo,
                _limits.MaxRequestBodySize,
                isConnect).ConfigureAwait(false);
        }
        catch (HttpRequestRejectedException)
        {
            // A request-parse interceptor refused this request (the pipeline already disposed the
            // partial body-wrapper chain and hook-attached features before surfacing). RFC 9114
            // §4.1 — abort the request stream (the ideal wire code is H3_REQUEST_REJECTED; the
            // IConnection abort contract resets with the transport's default stream error code)
            // rather than writing the h1-style status response. The QUIC connection and its other
            // streams are unaffected, so the caller drops just this stream and keeps serving.
            streamConnection.Abort();
            return null;
        }

        Http3Context context = new(request, response, connectionInfo, cancellationToken, streamConnection, features);

        // RFC 9218 §4 — the request's Priority header sets the effective priority.
        // Parsing is tolerant: a malformed value leaves the default (urgency 3,
        // non-incremental) in place.
        if (request.Headers.TryGetValue(HttpHeaderKey.Priority, out HttpHeaderValue priorityValue)
            && HttpPriority.TryParse(priorityValue, out HttpPriority headerPriority))
        {
            context.EffectivePriority = headerPriority;
        }

        // Expose the raw DATA-frame response body sink (over the QUIC stream, whose flow control
        // provides backpressure) and the exchange control to registered response interceptors so
        // feature packages (streaming / SSE, interim responses) can wrap them — without this
        // transport depending on any of those packages. The control's interim writes emit an
        // additional HEADERS frame on this request stream ahead of the final one (RFC 9114 §4.1);
        // its abort resets this stream, leaving the QUIC connection's other streams intact.
        if (_responseInterceptors.Length > 0)
        {
            context.RunResponseInterceptors(
                _responseInterceptors,
                new Http3ResponseBodyStream(context),
                new Http3ExchangeControl(this, context));
        }

        // Surface the :protocol pseudo-header (RFC 8441 / RFC 9220) generically
        // so a higher layer (Assimalign.Cohesion.Http.ExtendedConnect) can model
        // extended CONNECT without the transport interpreting it. Same Items key
        // convention as the HTTP/2 transport.
        if (extendedConnectProtocol is not null)
        {
            context.Items[TransportItemKeys.Protocol] = extendedConnectProtocol;
        }

        return context;
    }

    private static async Task WriteFrameAsync(Stream stream, Http3FrameType frameType, byte[] payload, CancellationToken cancellationToken)
    {
        QuicVariableLengthInteger.Write(stream, (long)frameType);
        QuicVariableLengthInteger.Write(stream, payload.Length);

        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        }
    }
    private static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        if (body is MemoryStream memoryStream)
        {
            return memoryStream.ToArray();
        }

        using MemoryStream copy = new();
        if (body.CanSeek)
        {
            body.Position = 0;
        }

        await body.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }
}
