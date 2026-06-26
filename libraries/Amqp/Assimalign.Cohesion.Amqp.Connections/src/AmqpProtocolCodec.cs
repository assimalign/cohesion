using System;

using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Encodes and decodes AMQP protocol headers.
/// </summary>
public static class AmqpProtocolCodec
{
    /// <summary>
    /// Encodes an AMQP protocol header to its eight-byte wire representation.
    /// </summary>
    /// <param name="header">The AMQP protocol header to encode.</param>
    /// <returns>The encoded AMQP protocol header bytes.</returns>
    public static byte[] Encode(AmqpProtocolHeader header)
    {
        return AmqpEncoding.EncodeProtocolHeader(header);
    }

    /// <summary>
    /// Decodes an AMQP protocol header from its wire representation.
    /// </summary>
    /// <param name="bytes">The AMQP protocol header bytes.</param>
    /// <returns>The decoded AMQP protocol header.</returns>
    public static AmqpProtocolHeader Decode(ReadOnlySpan<byte> bytes)
    {
        return AmqpEncoding.DecodeProtocolHeader(bytes);
    }
}
