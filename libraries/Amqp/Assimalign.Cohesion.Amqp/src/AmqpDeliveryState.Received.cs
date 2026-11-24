namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the <c>received</c> delivery state: how much of the message the receiver has, so a
/// resumed transfer can continue from there (AMQP 1.0 Part 3, section 3.4.1).
/// </summary>
/// <remarks>
/// The state is not terminal: it reports progress and never settles a delivery.
/// </remarks>
public sealed class AmqpReceived : AmqpDeliveryState
{
    /// <summary>
    /// Initializes a new received state.
    /// </summary>
    /// <param name="sectionNumber">The number of the first section the receiver does not have in full.</param>
    /// <param name="sectionOffset">The offset, in bytes, into that section of the first byte the receiver does not have.</param>
    public AmqpReceived(uint sectionNumber, ulong sectionOffset)
    {
        SectionNumber = sectionNumber;
        SectionOffset = sectionOffset;
    }

    /// <summary>
    /// Gets the number of the first section the receiver does not have in full.
    /// </summary>
    public uint SectionNumber { get; }

    /// <summary>
    /// Gets the offset, in bytes, into that section of the first byte the receiver does not have.
    /// </summary>
    public ulong SectionOffset { get; }

    /// <summary>
    /// Returns the state's name and position.
    /// </summary>
    /// <returns>The state's name, section number and offset.</returns>
    public override string ToString()
    {
        return "received (" + SectionNumber + ", " + SectionOffset + ")";
    }
}
