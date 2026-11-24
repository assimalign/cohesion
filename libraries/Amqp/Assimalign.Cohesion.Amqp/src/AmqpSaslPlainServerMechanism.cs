using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Offers the SASL <c>PLAIN</c> mechanism (RFC 4616): the client sends a user name and password, which an
/// application delegate validates.
/// </summary>
/// <remarks>
/// <c>PLAIN</c> sends the password in the clear, so offer it only over a carrier that encrypts, such as a
/// TLS layer. The authenticated identity is the authorization identity when the client named one and
/// the delegate accepted it, and otherwise the user name.
/// </remarks>
[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpSaslPlainServerMechanism : IAmqpSaslServerMechanism
{
    private readonly Func<AmqpSaslPlainCredentials, CancellationToken, ValueTask<bool>> _validate;

    private AmqpSaslPlainServerMechanism(Func<AmqpSaslPlainCredentials, CancellationToken, ValueTask<bool>> validate)
    {
        _validate = validate;
    }

    /// <summary>
    /// Gets the mechanism name, <c>PLAIN</c>.
    /// </summary>
    public AmqpSymbol Name => AmqpSaslMechanismNames.Plain;

    /// <summary>
    /// Creates a <c>PLAIN</c> mechanism that validates credentials with a delegate.
    /// </summary>
    /// <param name="validate">
    /// Returns <see langword="true"/> when the credentials are valid and, when the client named an
    /// authorization identity, the user may act as it.
    /// </param>
    /// <returns>The mechanism.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="validate"/> is <see langword="null"/>.</exception>
    public static AmqpSaslPlainServerMechanism Create(Func<AmqpSaslPlainCredentials, CancellationToken, ValueTask<bool>> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);

        return new AmqpSaslPlainServerMechanism(validate);
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

        return new AmqpSaslPlainServerExchange(_validate);
    }

    private sealed class DebugView
    {
        private readonly AmqpSaslPlainServerMechanism _mechanism;

        public DebugView(AmqpSaslPlainServerMechanism mechanism)
        {
            _mechanism = mechanism;
        }

        public AmqpSymbol Name => _mechanism.Name;
    }
}
