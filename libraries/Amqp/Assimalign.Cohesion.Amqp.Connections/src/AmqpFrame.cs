using System;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents a single AMQP frame.
/// </summary>
public sealed class AmqpFrame
{
    /// <summary>
    /// Initializes a new AMQP frame.
    /// </summary>
    /// <param name="channel">The AMQP channel.</param>
    /// <param name="performative">The frame performative.</param>
    /// <param name="payload">The frame payload that follows the performative.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="performative"/> is <see langword="null"/>.</exception>
    public AmqpFrame(ushort channel, AmqpPerformative performative, ReadOnlyMemory<byte> payload = default)
    {
        ArgumentNullException.ThrowIfNull(performative);

        Channel = channel;
        Performative = performative;
        Payload = payload;
    }

    /// <summary>
    /// Gets the AMQP channel number.
    /// </summary>
    public ushort Channel { get; }

    /// <summary>
    /// Gets the AMQP performative carried by the frame.
    /// </summary>
    public AmqpPerformative Performative { get; }

    /// <summary>
    /// Gets the AMQP payload that follows the performative.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; }
}
