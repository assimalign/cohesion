using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Offers the SASL <c>EXTERNAL</c> mechanism (RFC 4422, appendix A): the client is authenticated by a lower
/// layer, such as a TLS client certificate, and an application delegate maps what that layer established
/// to an identity.
/// </summary>
public sealed class AmqpSaslExternalServerMechanism : IAmqpSaslServerMechanism
{
    private readonly Func<IConnection, string?, CancellationToken, ValueTask<string?>> _authenticate;

    private AmqpSaslExternalServerMechanism(Func<IConnection, string?, CancellationToken, ValueTask<string?>> authenticate)
    {
        _authenticate = authenticate;
    }

    /// <summary>
    /// Gets the mechanism name, <c>EXTERNAL</c>.
    /// </summary>
    public AmqpSymbol Name => AmqpSaslMechanismNames.External;

    /// <summary>
    /// Creates an <c>EXTERNAL</c> mechanism that identifies clients with a delegate.
    /// </summary>
    /// <param name="authenticate">
    /// Receives the carrier connection, such as to read <c>connection.Tls?.RemoteCertificate</c>, and the
    /// authorization identity the client asked for (<see langword="null"/> when it asked for none). Returns
    /// the authenticated identity, or <see langword="null"/> to refuse the client.
    /// </param>
    /// <returns>The mechanism.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="authenticate"/> is <see langword="null"/>.</exception>
    public static AmqpSaslExternalServerMechanism Create(Func<IConnection, string?, CancellationToken, ValueTask<string?>> authenticate)
    {
        ArgumentNullException.ThrowIfNull(authenticate);

        return new AmqpSaslExternalServerMechanism(authenticate);
    }

    /// <summary>
    /// Creates the exchange that authenticates one connection.
    /// </summary>
    /// <param name="connection">The carrier connection being authenticated.</param>
    /// <param name="hostName">The host name the client's <c>sasl-init</c> named, or <see langword="null"/>.</param>
    /// <returns>The exchange.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> is <see langword="null"/>.</exception>
    public IAmqpSaslServerExchange CreateExchange(IConnection connection, string? hostName)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new AmqpSaslExternalServerExchange(connection, _authenticate);
    }
}
