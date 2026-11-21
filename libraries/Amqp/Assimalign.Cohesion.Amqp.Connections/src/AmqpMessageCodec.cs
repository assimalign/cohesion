using System;
using System.Buffers;
using System.Collections.Generic;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Encodes and decodes AMQP 1.0 messages (Part 3, section 3.2) apart from any link, such as for a
/// broker that stores the payloads it receives and forwards them later.
/// </summary>
/// <remarks>
/// <para>
/// A message is a sequence of sections: the header, delivery and message annotations, properties,
/// application properties, the body, and the footer. The properties, application properties and body
/// form the bare message, which the sender fixes; an intermediary may change only the annotated parts
/// around it. <see cref="Reannotate"/> replaces those parts and copies the bare message's bytes
/// unchanged, so a signature or digest over them stays valid.
/// </para>
/// <para>
/// Decoding validates the payload as <see cref="IAmqpDelivery.GetMessage"/> does: malformed sections fail
/// with an <see cref="AmqpException"/> whose <see cref="AmqpException.ErrorCode"/> is
/// <see cref="AmqpErrorCode.ProtocolViolation"/>. Finding the bare message, including for
/// <see cref="Reannotate"/>, holds the sections to the same order and multiplicity, so a payload that
/// passes through an intermediary is one a receiver can decode.
/// </para>
/// </remarks>
public static class AmqpMessageCodec
{
    /// <summary>
    /// Encodes a message.
    /// </summary>
    /// <param name="message">The message to encode.</param>
    /// <returns>The encoded message.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when a value in the message has no AMQP representation or nests too deeply, or an application property holds a list, map or array.</exception>
    public static byte[] Encode(AmqpMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using AmqpBufferWriter writer = new();

        AmqpMessageEncoding.Encode(writer, message);
        return writer.ToArray();
    }

    /// <summary>
    /// Encodes a message into a buffer writer.
    /// </summary>
    /// <param name="message">The message to encode.</param>
    /// <param name="destination">The writer that receives the encoded message.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> or <paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when a value in the message has no AMQP representation or nests too deeply, or an application property holds a list, map or array.</exception>
    public static void Encode(AmqpMessage message, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destination);

        using AmqpBufferWriter writer = new();

        AmqpMessageEncoding.Encode(writer, message);

        ReadOnlySpan<byte> encoded = writer.WrittenSpan;

        encoded.CopyTo(destination.GetSpan(encoded.Length));
        destination.Advance(encoded.Length);
    }

    /// <summary>
    /// Decodes a message.
    /// </summary>
    /// <param name="payload">The encoded message.</param>
    /// <returns>
    /// The decoded message. Its <c>data</c> body sections and <see cref="AmqpProperties.UserId"/> are
    /// slices of <paramref name="payload"/>, not copies, so the payload must not change while the
    /// message is in use.
    /// </returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.ProtocolViolation"/> when the payload is not a valid AMQP message.</exception>
    public static AmqpMessage Decode(ReadOnlyMemory<byte> payload)
    {
        return AmqpMessageEncoding.Decode(payload);
    }

    /// <summary>
    /// Returns the bare message: the properties, application properties and body sections, as encoded.
    /// </summary>
    /// <param name="payload">The encoded message.</param>
    /// <returns>A slice of <paramref name="payload"/>; empty when the message has none of those sections.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.ProtocolViolation"/> when the payload is not a valid sequence of message sections.</exception>
    public static ReadOnlyMemory<byte> GetBareMessage(ReadOnlyMemory<byte> payload)
    {
        (int offset, int length) = AmqpMessageEncoding.FindBareMessage(payload.Span);

        return payload.Slice(offset, length);
    }

    /// <summary>
    /// Re-encodes a message with a new header, new annotations and a new footer, copying its bare message
    /// unchanged, such as for a broker that counts a redelivery or annotates what it stored.
    /// </summary>
    /// <param name="payload">The encoded message.</param>
    /// <param name="header">The header to write, or <see langword="null"/> for none.</param>
    /// <param name="deliveryAnnotations">The delivery annotations to write, or <see langword="null"/> for none.</param>
    /// <param name="messageAnnotations">The message annotations to write, or <see langword="null"/> for none.</param>
    /// <param name="footer">The footer to write, or <see langword="null"/> for none.</param>
    /// <returns>The re-encoded message.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.ProtocolViolation"/> when the payload is not a valid sequence of message sections.</exception>
    /// <exception cref="ArgumentException">Thrown when an annotation value has no AMQP representation, or nests too deeply.</exception>
    public static byte[] Reannotate(
        ReadOnlyMemory<byte> payload,
        AmqpHeader? header,
        IReadOnlyDictionary<AmqpSymbol, object?>? deliveryAnnotations,
        IReadOnlyDictionary<AmqpSymbol, object?>? messageAnnotations,
        IReadOnlyDictionary<AmqpSymbol, object?>? footer)
    {
        (int offset, int length) = AmqpMessageEncoding.FindBareMessage(payload.Span);

        using AmqpBufferWriter writer = new(length + 64);

        if (header is not null)
        {
            AmqpMessageEncoding.WriteHeader(writer, header);
        }

        AmqpMessageEncoding.WriteAnnotations(writer, AmqpDescriptor.DeliveryAnnotations, deliveryAnnotations);
        AmqpMessageEncoding.WriteAnnotations(writer, AmqpDescriptor.MessageAnnotations, messageAnnotations);
        writer.Write(payload.Span.Slice(offset, length));
        AmqpMessageEncoding.WriteAnnotations(writer, AmqpDescriptor.Footer, footer);

        return writer.ToArray();
    }
}
