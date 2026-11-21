using System;
using System.Text;

using Assimalign.Cohesion.Amqp.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Authenticates with the SASL <c>EXTERNAL</c> mechanism (RFC 4422, appendix A): the server identifies the
/// client from a lower layer, such as the client certificate of a TLS layer.
/// </summary>
public sealed class AmqpSaslExternalClientMechanism : IAmqpSaslClientMechanism
{
    /// <summary>
    /// Initializes a new <c>EXTERNAL</c> mechanism.
    /// </summary>
    public AmqpSaslExternalClientMechanism()
    {
    }

    /// <summary>
    /// Gets the mechanism name, <c>EXTERNAL</c>.
    /// </summary>
    public AmqpSymbol Name => AmqpSaslMechanismNames.External;

    /// <summary>
    /// Gets or initializes the identity the client asks to act as, or <see langword="null"/> to act as the
    /// identity the lower layer established.
    /// </summary>
    public string? AuthorizationId { get; init; }

    /// <summary>
    /// Creates the exchange that authenticates one connection.
    /// </summary>
    /// <param name="connection">The carrier connection being authenticated.</param>
    /// <param name="hostName">The host name the client's <c>sasl-init</c> will name, or <see langword="null"/>.</param>
    /// <returns>The exchange.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> is <see langword="null"/>.</exception>
    public IAmqpSaslClientExchange CreateExchange(IConnection connection, string? hostName)
    {
        ArgumentNullException.ThrowIfNull(connection);

        byte[] response = Encoding.UTF8.GetBytes(AuthorizationId ?? string.Empty);

        return new AmqpSaslInitialResponseClientExchange(response, response);
    }
}
