using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

// The connection's output (#1384): every frame the connection writes — the pump's control frames, the
// transport's own answers, interim and final responses, tunnel octets — goes to one transport output, under
// the write gate, and every write and flush to it is held to the connection's output allowance. A peer that
// stops reading the connection stalls every writer at once, so the output, not one response, is what the
// rate is measured over.
internal sealed partial class Http2ConnectionContext
{
    private static readonly string _responseConnectionClosedMessage = ErrorMessages.Http2ResponseConnectionClosed;
    private static readonly string _responseDataRateTooLowMessage = ErrorMessages.Http2ResponseDataRateTooLow;
    private static readonly string _outputStalledMessage = ErrorMessages.Http2OutputStalled;

    // #1384 — the allowance of the connection's output: the listener's MinResponseDataRate measured over
    // every octet any writer hands to the output and every wait any writer spends on it. Null when the
    // listener holds responses to no rate. Only the holder of the write gate touches it.
    private readonly MinDataRateGate? _outputDataRate;
    // Set once the output stalled and the connection was aborted (FailOutputStall): every later write
    // fails at once rather than wait on a transport nothing drains.
    private volatile bool _outputStalled;

    /// <summary>
    /// Gets the most octets one DATA frame carries: the peer's <c>SETTINGS_MAX_FRAME_SIZE</c>, capped at
    /// <see cref="MinDataRateWait.MaxWriteSize"/> while the output or the response is held to a rate
    /// (#1384). A frame is credited whole as it is handed over, and the peer chooses its frame size, up to
    /// 16 MiB: uncapped, a peer that stops reading would be credited a 16 MiB frame up front, a stall the
    /// allowance would bound only after hours. Since <see cref="MinDataRateWait.MaxWriteSize"/> is the
    /// smallest frame size a peer may advertise (RFC 9113 §6.5.2), the cap is the default frame size.
    /// </summary>
    /// <param name="response">
    /// The exchange whose final response the frame carries, or <see langword="null"/> for a tunnel.
    /// </param>
    /// <returns>The DATA frame size to split on.</returns>
    private int GetMaxDataFrameSize(Http2Context? response)
    {
        int maxFrameSize = (int)_remoteSettings.MaxFrameSize;

        return _outputDataRate is not null || response?.GetResponseDataRateGate(_http2Limits.MinResponseDataRate, _timeProvider) is not null
            ? Math.Min(maxFrameSize, MinDataRateWait.MaxWriteSize)
            : maxFrameSize;
    }

    /// <summary>
    /// Writes one frame, with its payload, to the connection's output, within the output's allowance
    /// (<see cref="WriteOutputAsync"/>). For the pump's control frames, the transport's own answers and
    /// tunnel octets; a final response's frames go through <see cref="WriteResponseFrameAsync"/>.
    /// </summary>
    private ValueTask WriteFrameAsync(Http2Frame frame, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => WriteFrameAsync(frame, payload, cancellationToken, response: null);

    /// <summary>
    /// Writes a header block to the connection's output in one piece, within the output's allowance:
    /// RFC 9113 §6.10 — a HEADERS frame and its CONTINUATION frames are one header block no other frame
    /// may interrupt, so a cancellation lands before or after it.
    /// </summary>
    private ValueTask WriteHeaderBlockAsync(int streamId, byte[] headerBlock, bool endStream, CancellationToken cancellationToken)
        => WriteHeaderBlockAsync(streamId, headerBlock, endStream, cancellationToken, response: null);

    /// <summary>
    /// Flushes the connection's output within the output's allowance.
    /// </summary>
    private ValueTask FlushOutputAsync(CancellationToken cancellationToken)
        => FlushOutputAsync(cancellationToken, response: null);

    /// <summary>
    /// Writes a final response's HEADERS block (its head, or its trailer section) within the output's
    /// allowance, and credits the octets to the response's own (#1384).
    /// </summary>
    private ValueTask WriteResponseHeaderBlockAsync(Http2Context context, byte[] headerBlock, bool endStream, CancellationToken cancellationToken)
        => WriteHeaderBlockAsync(context.StreamId, headerBlock, endStream, cancellationToken, context);

    /// <summary>
    /// Writes one frame of a final response, with its payload, within the output's allowance, and credits
    /// the octets to the response's own (#1384).
    /// </summary>
    private ValueTask WriteResponseFrameAsync(Http2Context context, Http2Frame frame, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => WriteFrameAsync(frame, payload, cancellationToken, context);

    /// <summary>
    /// Flushes the connection's output for a final response within the output's allowance.
    /// </summary>
    private ValueTask FlushResponseOutputAsync(Http2Context context, CancellationToken cancellationToken)
        => FlushOutputAsync(cancellationToken, context);

    private ValueTask WriteFrameAsync(Http2Frame frame, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, Http2Context? response)
        => WriteOutputAsync(
            Http2FrameReader.HeaderLength + Http2FrameReader.GetPayloadFieldsLength(frame) + payload.Length,
            (Output: Stream, Frame: frame, Payload: payload),
            static (state, token) => new ValueTask(Http2FrameWriter.WriteAsync(state.Output, state.Frame, state.Payload, token)),
            cancellationToken,
            response);

    private ValueTask WriteHeaderBlockAsync(int streamId, byte[] headerBlock, bool endStream, CancellationToken cancellationToken, Http2Context? response)
    {
        int maxFrameSize = (int)_remoteSettings.MaxFrameSize;
        int frameCount = Math.Max(1, (headerBlock.Length + maxFrameSize - 1) / maxFrameSize);

        return WriteOutputAsync(
            (frameCount * Http2FrameReader.HeaderLength) + headerBlock.Length,
            (Output: Stream, StreamId: streamId, HeaderBlock: headerBlock, EndStream: endStream, MaxFrameSize: maxFrameSize),
            static (state, token) => new ValueTask(Http2FrameWriter.WriteHeaderBlockAsync(state.Output, state.StreamId, state.HeaderBlock, state.EndStream, state.MaxFrameSize, token)),
            cancellationToken,
            response);
    }

    private ValueTask FlushOutputAsync(CancellationToken cancellationToken, Http2Context? response)
        => WriteOutputAsync(
            0,
            Stream,
            static (output, token) => new ValueTask(output.FlushAsync(token)),
            cancellationToken,
            response);

    /// <summary>
    /// Runs one write or flush to the connection's output, which the caller holds the write gate for,
    /// within the output's allowance (#1384). The octets are credited as they are handed over, and a write
    /// still pending on the transport must complete within what is left of the allowance; when it does not,
    /// the output has stalled (<see cref="FailOutputStall"/>) and the write throws. Once the output has
    /// stalled, every later write throws at once without touching the transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The allowance is the connection's (<see cref="_outputDataRate"/>, the listener's
    /// <c>MinResponseDataRate</c> over the whole output): every frame any writer hands over earns it, and
    /// every wait any writer spends on the transport is charged to it. A write that waits while the
    /// transport drains other streams' octets is therefore measured against those octets too, so a small
    /// response behind a large one on a slow but steady reader is not mistaken for a stalled peer, while a
    /// peer that stops reading the connection is reclaimed whichever writer it stalls: the pump's
    /// <c>PING</c> acknowledgement as much as a response's DATA frame. The writer holding the gate never
    /// waits on anything but the transport, so bounding every write bounds every writer queued behind it.
    /// </para>
    /// <para>
    /// A final response's write (<paramref name="response"/>) also credits its octets to the response's
    /// own allowance, which bounds its wait for flow-control credit (<see cref="AcquireSendWindowAsync"/>);
    /// its first write resolves, and so freezes, the request's rate. When the listener holds the
    /// connection to no rate, a response that set one for itself is held to its own over its own writes.
    /// </para>
    /// </remarks>
    /// <param name="octets">The octets the write hands to the transport; 0 for a flush.</param>
    /// <param name="state">The write's arguments.</param>
    /// <param name="write">The write, given its arguments and the token it observes.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="response">
    /// The exchange whose final response is written, or <see langword="null"/> for any other frame. A
    /// stalled output fails a response's write with the exchange's cancellation, and any other with an
    /// <see cref="IOException"/>.
    /// </param>
    /// <returns>A task that completes once the transport accepted the write.</returns>
    private async ValueTask WriteOutputAsync<TState>(int octets, TState state, Func<TState, CancellationToken, ValueTask> write, CancellationToken cancellationToken, Http2Context? response)
    {
        if (_outputStalled)
        {
            throw CreateOutputStalledException(response);
        }

        MinDataRateGate? gate = _outputDataRate;

        if (response?.GetResponseDataRateGate(_http2Limits.MinResponseDataRate, _timeProvider) is { } responseGate)
        {
            if (gate is null)
            {
                gate = responseGate;
            }
            else
            {
                responseGate.Record(ticksWaited: 0, bytesTransferred: octets);
            }
        }

        CancellationToken closed = Connection.ConnectionClosed;
        using CancellationTokenSource? linked = response is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed, response.RequestCancelled)
            : cancellationToken.CanBeCanceled && cancellationToken != closed
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed)
                : null;
        CancellationToken writeToken = linked?.Token ?? closed;
        try
        {
            closed.ThrowIfCancellationRequested();
            if (gate is null)
            {
                await write(state, writeToken).ConfigureAwait(false);
            }
            else
            {
                await WriteOutputWithinRateAsync(gate, octets, state, write, writeToken, response).ConfigureAwait(false);
            }
            closed.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (CanReportPeerConnectionLoss
            && (IsWireLevelFailure(exception)
                || (closed.IsCancellationRequested && exception is OperationCanceledException or InvalidOperationException or ConnectionException)))
        {
            AbortResponsesOnPeerConnectionLoss();
            if (response is not null)
            {
                throw new OperationCanceledException(_responseConnectionClosedMessage, exception, response.RequestCancelled);
            }
            throw;
        }
        catch (OperationCanceledException exception) when (response?.RequestCancelled.IsCancellationRequested == true)
        {
            throw new OperationCanceledException(exception.Message, exception, response.RequestCancelled);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested
            && exception.CancellationToken != cancellationToken)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
    }

    private async ValueTask WriteOutputWithinRateAsync<TState>(MinDataRateGate gate, int octets, TState state, Func<TState, CancellationToken, ValueTask> write, CancellationToken cancellationToken, Http2Context? response)
    {
        gate.Record(ticksWaited: 0, bytesTransferred: octets);

        if (!await MinDataRateWait.TryRunAsync(gate, state, write, cancellationToken).ConfigureAwait(false))
        {
            throw FailOutputStall(response);
        }
    }

    /// <summary>
    /// Ends a connection whose output stopped draining below the minimum response data rate (#1384).
    /// Nothing more can be written, a <c>GOAWAY</c> included, so the connection is aborted, and the frame
    /// pump stops, which aborts every exchange the connection carries and ends the receive enumeration.
    /// Every writer queued for the write gate then fails at once (<see cref="WriteOutputAsync"/>).
    /// </summary>
    /// <param name="response">The exchange whose final response stalled, or <see langword="null"/> for any other writer.</param>
    /// <returns>The exception the stalled writer throws.</returns>
    private Exception FailOutputStall(Http2Context? response)
    {
        _outputStalled = true;
        if (response is null)
        {
            HttpTransportEventSource.Log.ResponseRateAborted(Connection.Id, "2", -1);
        }
        else
        {
            response.Stream.ReportResponseRateAborted();
        }
        response?.LatchResponseAbortedByClient();
        LatchResponsesAbortedByClient();
        Connection.Abort(new IOException(_responseDataRateTooLowMessage));
        StopPumpOnStall();
        return CreateOutputStalledException(response);
    }

    private static Exception CreateOutputStalledException(Http2Context? response)
    {
        return response is null
            ? new IOException(_outputStalledMessage)
            : response.AbortSlowResponse();
    }
}
