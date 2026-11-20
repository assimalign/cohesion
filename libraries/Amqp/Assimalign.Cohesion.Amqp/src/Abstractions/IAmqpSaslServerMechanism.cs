using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a SASL mechanism a listening endpoint offers (AMQP 1.0 Part 5, section 5.3).
/// </summary>
/// <remarks>
/// The listener lists every configured mechanism's <see cref="Name"/> in its <c>sasl-mechanisms</c> frame.
/// When the client selects one, the listener creates an exchange for that connection and drives it through
/// the client's responses until it produces an outcome. A mechanism is shared by every connection, so it
/// keeps per-connection state in the exchange, never in itself.
/// </remarks>
public interface IAmqpSaslServerMechanism
{
    /// <summary>
    /// Gets the mechanism's registered SASL name, such as <c>PLAIN</c>.
    /// </summary>
    AmqpSymbol Name { get; }

    /// <summary>
    /// Creates the exchange that authenticates one connection.
    /// </summary>
    /// <param name="connection">
    /// The carrier connection being authenticated, so a mechanism can use what a lower layer established,
    /// such as the client certificate a TLS layer registered.
    /// </param>
    /// <param name="hostName">The host name the client's <c>sasl-init</c> named, or <see langword="null"/>.</param>
    /// <returns>The exchange.</returns>
    IAmqpSaslServerExchange CreateExchange(IConnection connection, string? hostName);
}
