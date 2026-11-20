using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a SASL mechanism a connecting endpoint can authenticate with (AMQP 1.0 Part 5, section 5.3).
/// </summary>
/// <remarks>
/// The factory selects the first of its configured mechanisms whose <see cref="Name"/> the server's
/// <c>sasl-mechanisms</c> frame offers, creates an exchange for that connection, and drives it until the
/// server sends its outcome. A mechanism is shared by every connection, so it keeps per-connection state
/// in the exchange, never in itself.
/// </remarks>
public interface IAmqpSaslClientMechanism
{
    /// <summary>
    /// Gets the mechanism's registered SASL name, such as <c>PLAIN</c>.
    /// </summary>
    AmqpSymbol Name { get; }

    /// <summary>
    /// Creates the exchange that authenticates one connection.
    /// </summary>
    /// <param name="connection">The carrier connection being authenticated.</param>
    /// <param name="hostName">The host name the client's <c>sasl-init</c> will name, or <see langword="null"/>.</param>
    /// <returns>The exchange.</returns>
    IAmqpSaslClientExchange CreateExchange(IConnection connection, string? hostName);
}
