using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Reads the frames of one HTTP/3 request stream (RFC 9114 §4.1) incrementally off the stream's
/// <see cref="PipeReader"/>: frame headers, bounded HEADERS payloads, skipped frames, and DATA payloads
/// copied straight into the caller's buffer.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is read ahead of demand. A HEADERS payload is the only thing buffered whole, and only after
/// its declared length has passed the configured limit; DATA octets move from the pipe into the reader's
/// buffer as they are asked for, and a skipped frame's payload is consumed and discarded as it arrives.
/// Octets nobody has asked for stay in the QUIC stream's receive buffer, so the peer is paced by QUIC
/// flow control (RFC 9000 §4) instead of by server memory.
/// </para>
/// <para>
/// Every frame length is bounds-checked against the stream itself: a stream that ends cleanly inside a
/// frame — its header or its payload — is an <c>H3_FRAME_ERROR</c> connection error (RFC 9114 §7.1),
/// raised as <see cref="Http3ConnectionException"/>. A HEADERS frame longer than the configured limit
/// is refused before any of it is buffered, as an <c>H3_FRAME_ERROR</c> stream error
/// (<see cref="Http3StreamException"/>).
/// </para>
/// <para>
/// Single-reader: the connection context's per-stream processing reads the request head, then the
/// request body (<see cref="Http3RequestBodyStream"/>) continues from the same position. The two never
/// overlap.
/// </para>
/// </remarks>
internal sealed class Http3RequestStreamReader
{
    private readonly PipeReader _input;

    /// <summary>
    /// Initializes a reader over a request stream's input.
    /// </summary>
    /// <param name="input">The request stream's inbound pipe.</param>
    public Http3RequestStreamReader(PipeReader input)
    {
        _input = input;
    }

    /// <summary>
    /// Gets the request stream's inbound pipe.
    /// </summary>
    public PipeReader Input => _input;

    /// <summary>
    /// Gets a value indicating whether the stream ended cleanly on a frame boundary — the peer's FIN
    /// was read, so no further frame can arrive.
    /// </summary>
    public bool IsCompleted { get; private set; }

    /// <summary>
    /// Determines whether a known frame type is prohibited on a request stream: the control-stream
    /// frames (CANCEL_PUSH, SETTINGS, GOAWAY, MAX_PUSH_ID — RFC 9114 §7.2.3 / §7.2.4 / §7.2.6 /
    /// §7.2.7), PUSH_PROMISE, which a client never sends (§7.2.5), the reserved HTTP/2 frame types
    /// (§7.2.8), and PRIORITY_UPDATE, which belongs on the control stream (RFC 9218 §7.2). Receiving
    /// one is an <c>H3_FRAME_UNEXPECTED</c> connection error. Frames of unknown type are not
    /// prohibited — they are skipped (RFC 9114 §9).
    /// </summary>
    /// <param name="frameType">The frame type read off the request stream.</param>
    /// <returns><see langword="true"/> when the frame type must not appear on a request stream.</returns>
    public static bool IsProhibitedOnRequestStream(long frameType)
    {
        switch (frameType)
        {
            case (long)Http3FrameType.ReservedHttp2Priority:
            case (long)Http3FrameType.CancelPush:
            case (long)Http3FrameType.Settings:
            case (long)Http3FrameType.PushPromise:
            case (long)Http3FrameType.ReservedHttp2Ping:
            case (long)Http3FrameType.GoAway:
            case (long)Http3FrameType.ReservedHttp2WindowUpdate:
            case (long)Http3FrameType.ReservedHttp2Continuation:
            case (long)Http3FrameType.MaxPushId:
            case (long)Http3FrameType.PriorityUpdateRequest:
            case (long)Http3FrameType.PriorityUpdatePush:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads the request stream up to and including its HEADERS frame and returns the encoded field
    /// section (RFC 9114 §4.1). Frames of unknown or reserved type ahead of it are skipped (§9); a
    /// DATA frame or a prohibited frame ahead of it is an invalid frame sequence.
    /// </summary>
    /// <param name="maxHeadersFrameSize">
    /// The largest encoded HEADERS payload, in octets, the server buffers
    /// (<see cref="Http3ConnectionListenerOptions.Http3Limits.MaxRequestHeadersFrameSize"/>). The decoded size is
    /// bounded separately, by <see cref="Http3QPackOptions.MaxFieldSectionSize"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>
    /// The encoded field section, or <see langword="null"/> when the stream ended cleanly before any
    /// HEADERS frame arrived.
    /// </returns>
    /// <exception cref="Http3ConnectionException">
    /// Thrown (<c>H3_FRAME_UNEXPECTED</c>) for a DATA or prohibited frame ahead of the HEADERS frame, or
    /// (<c>H3_FRAME_ERROR</c>) when the stream ends inside a frame.
    /// </exception>
    /// <exception cref="Http3StreamException">
    /// Thrown (<c>H3_FRAME_ERROR</c>) when the HEADERS frame is longer than <paramref name="maxHeadersFrameSize"/>.
    /// </exception>
    public async ValueTask<byte[]?> ReadHeaderSectionAsync(int maxHeadersFrameSize, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await ReadFrameHeaderAsync(cancellationToken).ConfigureAwait(false) is not { } frame)
            {
                return null;
            }

            if (frame.Type == (long)Http3FrameType.Headers)
            {
                return await ReadFieldSectionAsync(frame, maxHeadersFrameSize, cancellationToken).ConfigureAwait(false);
            }

            if (frame.Type == (long)Http3FrameType.Data)
            {
                throw new Http3ConnectionException(
                    Http3ErrorCode.UnexpectedFrame,
                    "An HTTP/3 request stream carried a DATA frame before its HEADERS frame (RFC 9114 §4.1).");
            }

            if (IsProhibitedOnRequestStream(frame.Type))
            {
                throw new Http3ConnectionException(
                    Http3ErrorCode.UnexpectedFrame,
                    $"Frame type 0x{frame.Type:x} is not permitted on an HTTP/3 request stream (RFC 9114 §7.2).");
            }

            await SkipPayloadAsync(frame.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the payload of a HEADERS frame whose header has just been read — the encoded field
    /// section of the request head or of its trailer section.
    /// </summary>
    /// <param name="frame">The HEADERS frame header.</param>
    /// <param name="maxHeadersFrameSize">
    /// The largest encoded HEADERS payload, in octets, the server buffers
    /// (<see cref="Http3ConnectionListenerOptions.Http3Limits.MaxRequestHeadersFrameSize"/>). The decoded size is
    /// bounded separately, by <see cref="Http3QPackOptions.MaxFieldSectionSize"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The encoded field section.</returns>
    /// <exception cref="Http3StreamException">
    /// Thrown (<c>H3_FRAME_ERROR</c>) when the frame is longer than <paramref name="maxHeadersFrameSize"/>;
    /// nothing of it has been buffered.
    /// </exception>
    /// <exception cref="Http3ConnectionException">
    /// Thrown (<c>H3_FRAME_ERROR</c>) when the stream ends before the payload is complete (RFC 9114 §7.1).
    /// </exception>
    public ValueTask<byte[]> ReadFieldSectionAsync(Http3FrameHeader frame, int maxHeadersFrameSize, CancellationToken cancellationToken)
    {
        if (frame.Length > maxHeadersFrameSize)
        {
            throw new Http3StreamException(
                Http3ErrorCode.FrameError,
                $"An HTTP/3 HEADERS frame of {frame.Length} octets exceeds the {maxHeadersFrameSize}-octet limit (MaxRequestHeadersFrameSize).");
        }

        return ReadPayloadAsync((int)frame.Length, cancellationToken);
    }

    /// <summary>
    /// Reads the next frame header (type and length).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>
    /// The frame header, or <see langword="null"/> when the stream ended cleanly on a frame boundary
    /// (after which <see cref="IsCompleted"/> is <see langword="true"/>).
    /// </returns>
    /// <exception cref="Http3ConnectionException">
    /// Thrown (<c>H3_FRAME_ERROR</c>) when the stream ends inside a frame header (RFC 9114 §7.1).
    /// </exception>
    public async ValueTask<Http3FrameHeader?> ReadFrameHeaderAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (TryParseFrameHeader(buffer, out Http3FrameHeader frame, out SequencePosition consumed))
            {
                _input.AdvanceTo(consumed);
                return frame;
            }

            if (result.IsCompleted)
            {
                _input.AdvanceTo(buffer.End);

                if (buffer.IsEmpty)
                {
                    IsCompleted = true;
                    return null;
                }

                throw Truncated("frame header");
            }

            // Not enough octets for both varints yet: mark everything examined so the next read waits
            // for more instead of returning the same partial header.
            _input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Reads and discards exactly <paramref name="length"/> payload octets (a frame of unknown or
    /// reserved type, RFC 9114 §9), consuming them as they arrive without buffering the frame.
    /// </summary>
    /// <param name="length">The payload length to discard.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A task that completes when the payload has been discarded.</returns>
    /// <exception cref="Http3ConnectionException">
    /// Thrown (<c>H3_FRAME_ERROR</c>) when the stream ends before the payload is complete (RFC 9114 §7.1).
    /// </exception>
    public async ValueTask SkipPayloadAsync(long length, CancellationToken cancellationToken)
    {
        long remaining = length;

        while (remaining > 0)
        {
            remaining -= await SkipAvailableAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Discards the payload octets that are available now — waiting for at least one if none are — up
    /// to <paramref name="maxOctets"/>. The resumable form of <see cref="SkipPayloadAsync"/>: a caller
    /// that keeps the remaining count in its own state can be cancelled between calls without losing
    /// its place in the frame.
    /// </summary>
    /// <param name="maxOctets">The payload octets still to discard (greater than zero).</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The number of octets discarded (at least one).</returns>
    /// <exception cref="Http3ConnectionException">
    /// Thrown (<c>H3_FRAME_ERROR</c>) when the stream ends before the payload is complete (RFC 9114 §7.1).
    /// </exception>
    public async ValueTask<long> SkipAvailableAsync(long maxOctets, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (!buffer.IsEmpty)
            {
                long take = Math.Min(buffer.Length, maxOctets);
                _input.AdvanceTo(buffer.GetPosition(take));
                return take;
            }

            if (result.IsCompleted)
            {
                _input.AdvanceTo(buffer.End);
                throw Truncated("frame payload");
            }

            _input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Copies the next available payload octets of the current DATA frame into
    /// <paramref name="destination"/>. The caller bounds <paramref name="destination"/> by the frame's
    /// remaining length, so the read never crosses into the next frame.
    /// </summary>
    /// <param name="destination">A non-empty buffer no longer than the frame's remaining payload.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The number of octets copied (at least one).</returns>
    /// <exception cref="Http3ConnectionException">
    /// Thrown (<c>H3_FRAME_ERROR</c>) when the stream ends before the frame's payload is complete
    /// (RFC 9114 §7.1).
    /// </exception>
    public async ValueTask<int> ReadDataAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (!buffer.IsEmpty)
            {
                int count = (int)Math.Min(buffer.Length, destination.Length);
                ReadOnlySequence<byte> octets = buffer.Slice(0, count);
                octets.CopyTo(destination.Span);
                _input.AdvanceTo(octets.End);
                return count;
            }

            if (result.IsCompleted)
            {
                _input.AdvanceTo(buffer.End);
                throw Truncated("DATA frame");
            }

            _input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private async ValueTask<byte[]> ReadPayloadAsync(int length, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return [];
        }

        while (true)
        {
            ReadResult result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= length)
            {
                ReadOnlySequence<byte> payload = buffer.Slice(0, length);
                byte[] octets = payload.ToArray();
                _input.AdvanceTo(payload.End);
                return octets;
            }

            if (result.IsCompleted)
            {
                _input.AdvanceTo(buffer.End);
                throw Truncated("HEADERS frame");
            }

            _input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool TryParseFrameHeader(ReadOnlySequence<byte> buffer, out Http3FrameHeader frame, out SequencePosition consumed)
    {
        frame = default;
        consumed = buffer.Start;

        if (!QuicVariableLengthInteger.TryDecode(buffer, out long type, out SequencePosition afterType)
            || !QuicVariableLengthInteger.TryDecode(buffer.Slice(afterType), out long length, out SequencePosition afterLength))
        {
            return false;
        }

        frame = new Http3FrameHeader(type, length);
        consumed = afterLength;
        return true;
    }

    private static Http3ConnectionException Truncated(string what)
    {
        // RFC 9114 §7.1 — "When a stream terminates cleanly, if the last frame on the stream was
        // truncated, this MUST be treated as a connection error of type H3_FRAME_ERROR."
        return new Http3ConnectionException(
            Http3ErrorCode.FrameError,
            $"The HTTP/3 request stream ended inside a {what}: the frame length exceeds the octets the stream carried (RFC 9114 §7.1).");
    }
}
