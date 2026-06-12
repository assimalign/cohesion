namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Describes which halves of a connection's duplex pipe are usable.
/// </summary>
/// <remarks>
/// Stream transports such as TCP always produce <see cref="Bidirectional"/> connections. Multiplexed
/// transports such as QUIC can produce unidirectional streams: an inbound unidirectional stream is
/// <see cref="ReadOnly"/> (its output throws), and an outbound unidirectional stream is
/// <see cref="WriteOnly"/> (its input is pre-completed and yields no data).
/// </remarks>
public enum ConnectionDirection
{
    /// <summary>
    /// Both halves are usable: data can be read from the input and written to the output.
    /// </summary>
    Bidirectional = 0,

    /// <summary>
    /// Only the input is usable; writing to the output throws.
    /// </summary>
    ReadOnly = 1,

    /// <summary>
    /// Only the output is usable; the input is pre-completed and yields no data.
    /// </summary>
    WriteOnly = 2
}
