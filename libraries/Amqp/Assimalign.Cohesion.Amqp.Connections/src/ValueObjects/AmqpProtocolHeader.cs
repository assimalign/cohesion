using System;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents an AMQP eight-byte protocol header.
/// </summary>
public readonly struct AmqpProtocolHeader : IEquatable<AmqpProtocolHeader>
{
    /// <summary>
    /// Initializes a new AMQP protocol header.
    /// </summary>
    /// <param name="protocolId">The negotiated protocol identifier.</param>
    /// <param name="major">The major version.</param>
    /// <param name="minor">The minor version.</param>
    /// <param name="revision">The revision version.</param>
    public AmqpProtocolHeader(AmqpProtocolId protocolId, byte major, byte minor, byte revision)
    {
        ProtocolId = protocolId;
        Major = major;
        Minor = minor;
        Revision = revision;
    }

    /// <summary>
    /// Gets the negotiated protocol identifier.
    /// </summary>
    public AmqpProtocolId ProtocolId { get; }

    /// <summary>
    /// Gets the major version.
    /// </summary>
    public byte Major { get; }

    /// <summary>
    /// Gets the minor version.
    /// </summary>
    public byte Minor { get; }

    /// <summary>
    /// Gets the revision version.
    /// </summary>
    public byte Revision { get; }

    /// <summary>
    /// Gets the frame type used for the current protocol identifier.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the protocol header does not carry frames directly.</exception>
    public AmqpFrameType FrameType => ProtocolId switch
    {
        AmqpProtocolId.Amqp => AmqpFrameType.Amqp,
        AmqpProtocolId.Sasl => AmqpFrameType.Sasl,
        _ => throw new InvalidOperationException("The selected AMQP protocol identifier does not map directly to an AMQP frame type.")
    };

    /// <summary>
    /// Gets the default AMQP 1.0 protocol header.
    /// </summary>
    public static AmqpProtocolHeader Amqp10 => new(AmqpProtocolId.Amqp, 1, 0, 0);

    /// <summary>
    /// Gets the default SASL 1.0 protocol header.
    /// </summary>
    public static AmqpProtocolHeader Sasl10 => new(AmqpProtocolId.Sasl, 1, 0, 0);

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is AmqpProtocolHeader other && Equals(other);
    }

    /// <inheritdoc />
    public bool Equals(AmqpProtocolHeader other)
    {
        return ProtocolId == other.ProtocolId &&
            Major == other.Major &&
            Minor == other.Minor &&
            Revision == other.Revision;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine((byte) ProtocolId, Major, Minor, Revision);
    }

    /// <summary>
    /// Compares two AMQP protocol headers for equality.
    /// </summary>
    public static bool operator ==(AmqpProtocolHeader left, AmqpProtocolHeader right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two AMQP protocol headers for inequality.
    /// </summary>
    public static bool operator !=(AmqpProtocolHeader left, AmqpProtocolHeader right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{ProtocolId}/{Major}.{Minor}.{Revision}";
    }
}
