namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Defines the role of a link endpoint (AMQP 1.0 Part 2, section 2.8.1).
/// </summary>
/// <remarks>
/// The wire encodes the role as a boolean: <see langword="false"/> for <see cref="Sender"/> and
/// <see langword="true"/> for <see cref="Receiver"/>.
/// </remarks>
public enum AmqpRole
{
    /// <summary>
    /// The endpoint sends messages on the link.
    /// </summary>
    Sender = 0,

    /// <summary>
    /// The endpoint receives messages on the link.
    /// </summary>
    Receiver = 1
}
