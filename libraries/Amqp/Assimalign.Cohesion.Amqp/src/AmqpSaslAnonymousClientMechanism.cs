using System;
using System.Text;

using Assimalign.Cohesion.Amqp.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Authenticates with the SASL <c>ANONYMOUS</c> mechanism (RFC 4505): the client asks for access without an
/// identity, optionally sending trace information.
/// </summary>
public sealed class AmqpSaslAnonymousClientMechanism : IAmqpSaslClientMechanism
{
    /// <summary>
    /// Initializes a new <c>ANONYMOUS</c> mechanism.
    /// </summary>
    public AmqpSaslAnonymousClientMechanism()
    {
    }

    /// <summary>
    /// Gets the mechanism name, <c>ANONYMOUS</c>.
    /// </summary>
    public AmqpSymbol Name => AmqpSaslMechanismNames.Anonymous;

    /// <summary>
    /// Gets or initializes the trace information the client sends, such as a contact address, or
    /// <see langword="null"/> to send none.
    /// </summary>
    public string? Trace { get; init; }

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

        byte[] trace = Encoding.UTF8.GetBytes(Trace ?? string.Empty);

        return new AmqpSaslInitialResponseClientExchange(trace, trace);
    }
}
