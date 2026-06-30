using System;

using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Encodes and decodes AMQP 1.0 messages.
/// </summary>
public static class AmqpMessageCodec
{
    /// <summary>
    /// Encodes an AMQP message to its section-based wire representation.
    /// </summary>
    /// <param name="message">The AMQP message to encode.</param>
    /// <returns>The encoded message bytes.</returns>
    public static byte[] Encode(AmqpMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return AmqpEncoding.EncodeMessage(message);
    }

    /// <summary>
    /// Decodes an AMQP message from its section-based wire representation.
    /// </summary>
    /// <param name="bytes">The AMQP message bytes.</param>
    /// <returns>The decoded AMQP message.</returns>
    public static AmqpMessage Decode(ReadOnlySpan<byte> bytes)
    {
        return AmqpEncoding.DecodeMessage(bytes);
    }
}
