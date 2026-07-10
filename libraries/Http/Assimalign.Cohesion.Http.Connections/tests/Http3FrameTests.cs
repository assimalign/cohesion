using System;

using Assimalign.Cohesion.Http.Connections.Internal.Http3;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class Http3FrameTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: GOAWAY frame should encode a single-byte stream id as a varint payload")]
    public void Http3GoAwayFrame_OnSmallStreamId_ShouldEncodeVarintPayload()
    {
        // RFC 9114 §7.2.6 — GOAWAY is frame type 0x07, a length prefix, then the
        // stream id as a QUIC variable-length integer (§5.2). A stream id below
        // 64 encodes as one octet, so the frame is exactly [0x07, 0x01, id] —
        // and critically the Length is NON-zero (the precursor was a malformed
        // zero-length frame).
        byte[] frame = Http3GoAwayFrame.Encode(4);

        frame.ShouldBe(new byte[] { 0x07, 0x01, 0x04 });

        int index = 0;
        long frameType = QuicVariableLengthInteger.Decode(frame, ref index);
        long length = QuicVariableLengthInteger.Decode(frame, ref index);
        long streamId = QuicVariableLengthInteger.Decode(frame, ref index);

        frameType.ShouldBe((long)Http3FrameType.GoAway);
        length.ShouldBe(1L);
        streamId.ShouldBe(4L);
        index.ShouldBe(frame.Length); // every octet accounted for
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: GOAWAY frame should encode a multi-byte stream id as a two-octet varint")]
    public void Http3GoAwayFrame_OnLargeStreamId_ShouldEncodeTwoOctetVarint()
    {
        // RFC 9000 §16 — a value in [64, 16383] uses the 2-octet varint form
        // (0x40 prefix), so 8192 encodes as 0x60 0x00 and the frame Length is 2.
        byte[] frame = Http3GoAwayFrame.Encode(8192);

        frame.ShouldBe(new byte[] { 0x07, 0x02, 0x60, 0x00 });

        int index = 0;
        long frameType = QuicVariableLengthInteger.Decode(frame, ref index);
        long length = QuicVariableLengthInteger.Decode(frame, ref index);
        long streamId = QuicVariableLengthInteger.Decode(frame, ref index);

        frameType.ShouldBe((long)Http3FrameType.GoAway);
        length.ShouldBe(2L);
        streamId.ShouldBe(8192L);
        index.ShouldBe(frame.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: GOAWAY frame should encode stream id zero with a non-zero length")]
    public void Http3GoAwayFrame_OnZeroStreamId_ShouldStillCarryNonZeroLength()
    {
        // A GOAWAY announcing stream id 0 (no request processed) still carries a
        // one-octet payload — the frame is [0x07, 0x01, 0x00], never a bare
        // zero-length frame.
        byte[] frame = Http3GoAwayFrame.Encode(0);

        frame.ShouldBe(new byte[] { 0x07, 0x01, 0x00 });
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: GOAWAY frame should reject a negative stream id")]
    public void Http3GoAwayFrame_OnNegativeStreamId_ShouldThrow()
    {
        // QUIC stream identifiers are unsigned varints; a negative value is a
        // programming error, not a wire value.
        Should.Throw<ArgumentOutOfRangeException>(() => Http3GoAwayFrame.Encode(-1));
    }
}
