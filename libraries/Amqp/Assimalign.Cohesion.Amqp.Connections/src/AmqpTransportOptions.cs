using System;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Configures AMQP transport behavior layered on top of a lower-level carrier transport.
/// </summary>
public sealed class AmqpTransportOptions
{
    private uint _maxFrameSize = 262_144u;

    /// <summary>
    /// Gets or sets the initial AMQP protocol header sent when the connection context is opened.
    /// </summary>
    public AmqpProtocolHeader InitialProtocolHeader { get; set; } = AmqpProtocolHeader.Amqp10;

    /// <summary>
    /// Gets or sets a value indicating whether the AMQP protocol header should be negotiated automatically when the connection context opens.
    /// </summary>
    public bool AutoNegotiateProtocolHeader { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum AMQP frame size used when sending frames.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than 512 bytes.</exception>
    public uint MaxFrameSize
    {
        get => _maxFrameSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 512u);

            _maxFrameSize = value;
        }
    }
}
