using System;

using Assimalign.Cohesion.Amqp.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Offers the SASL <c>ANONYMOUS</c> mechanism (RFC 4505): every client is accepted without an identity.
/// </summary>
public sealed class AmqpSaslAnonymousServerMechanism : IAmqpSaslServerMechanism
{
    /// <summary>
    /// Initializes a new <c>ANONYMOUS</c> mechanism.
    /// </summary>
    public AmqpSaslAnonymousServerMechanism()
    {
    }

    /// <summary>
    /// Gets the mechanism name, <c>ANONYMOUS</c>.
    /// </summary>
    public AmqpSymbol Name => AmqpSaslMechanismNames.Anonymous;

    /// <summary>
    /// Creates the exchange that accepts one connection.
    /// </summary>
    /// <param name="connection">The carrier connection being authenticated.</param>
    /// <param name="hostName">The host name the client's <c>sasl-init</c> named, or <see langword="null"/>.</param>
    /// <returns>The exchange.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> is <see langword="null"/>.</exception>
    public IAmqpSaslServerExchange CreateExchange(IConnection connection, string? hostName)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return AmqpSaslAnonymousServerExchange.Instance;
    }
}
