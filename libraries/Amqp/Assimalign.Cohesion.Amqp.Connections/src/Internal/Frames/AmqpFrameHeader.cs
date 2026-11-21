namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The fixed eight-byte header of an AMQP frame (AMQP 1.0 Part 2, section 2.3.1).
/// </summary>
/// <param name="Size">The size of the whole frame, header included.</param>
/// <param name="DataOffset">The offset of the frame body in four-byte words.</param>
/// <param name="Type">The frame type: <see cref="AmqpFrame.AmqpType"/> or <see cref="AmqpFrame.SaslType"/>.</param>
/// <param name="Channel">The channel the frame travels on.</param>
internal readonly record struct AmqpFrameHeader(uint Size, byte DataOffset, byte Type, ushort Channel);
