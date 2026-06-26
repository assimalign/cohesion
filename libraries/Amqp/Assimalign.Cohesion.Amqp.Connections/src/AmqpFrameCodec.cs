using System;

using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Encodes and decodes AMQP frames.
/// </summary>
public static class AmqpFrameCodec
{
    /// <summary>
    /// Encodes an AMQP frame to its wire representation.
    /// </summary>
    /// <param name="frame">The AMQP frame to encode.</param>
    /// <param name="frameType">The AMQP frame type for the current protocol phase.</param>
    /// <param name="maxFrameSize">The maximum encoded AMQP frame size.</param>
    /// <returns>The encoded AMQP frame bytes.</returns>
    public static byte[] Encode(AmqpFrame frame, AmqpFrameType frameType = AmqpFrameType.Amqp, uint maxFrameSize = 262_144u)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return AmqpEncoding.EncodeFrame(frame, frameType, maxFrameSize);
    }

    /// <summary>
    /// Decodes an AMQP frame from its wire representation.
    /// </summary>
    /// <param name="bytes">The AMQP frame bytes.</param>
    /// <param name="frameType">The AMQP frame type for the current protocol phase.</param>
    /// <returns>The decoded AMQP frame.</returns>
    public static AmqpFrame Decode(ReadOnlySpan<byte> bytes, AmqpFrameType frameType = AmqpFrameType.Amqp)
    {
        return AmqpEncoding.DecodeFrame(bytes, frameType);
    }
}
