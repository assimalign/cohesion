using System;
using System.Text;

using Assimalign.Cohesion.Amqp.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Authenticates with the SASL <c>PLAIN</c> mechanism (RFC 4616): a user name and password, and optionally
/// an identity to act as.
/// </summary>
/// <remarks>
/// <c>PLAIN</c> sends the password in the clear, so use it only over a carrier that encrypts, such as a TLS
/// layer.
/// </remarks>
public sealed class AmqpSaslPlainClientMechanism : IAmqpSaslClientMechanism
{
    private readonly byte[] _response;

    private AmqpSaslPlainClientMechanism(byte[] response)
    {
        _response = response;
    }

    /// <summary>
    /// Gets the mechanism name, <c>PLAIN</c>.
    /// </summary>
    public AmqpSymbol Name => AmqpSaslMechanismNames.Plain;

    /// <summary>
    /// Creates a <c>PLAIN</c> mechanism for a user.
    /// </summary>
    /// <param name="userName">The authentication identity.</param>
    /// <param name="password">The password.</param>
    /// <param name="authorizationId">The identity to act as, or <see langword="null"/> to act as <paramref name="userName"/>.</param>
    /// <returns>The mechanism.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="userName"/> or <paramref name="password"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="userName"/> or <paramref name="password"/> is empty, or any value contains a NUL character.</exception>
    public static AmqpSaslPlainClientMechanism Create(string userName, string password, string? authorizationId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);

        if (userName.Contains('\0'))
        {
            throw new ArgumentException(ErrorMessages.PlainCredentialInvalid, nameof(userName));
        }

        if (password.Contains('\0'))
        {
            throw new ArgumentException(ErrorMessages.PlainCredentialInvalid, nameof(password));
        }

        if (authorizationId is not null && authorizationId.Contains('\0'))
        {
            throw new ArgumentException(ErrorMessages.PlainCredentialInvalid, nameof(authorizationId));
        }

        // message = [authzid] NUL authcid NUL passwd (RFC 4616, section 2).
        string message = string.Concat(authorizationId, "\0", userName, "\0", password);

        return new AmqpSaslPlainClientMechanism(Encoding.UTF8.GetBytes(message));
    }

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

        // A server that wants the response after an empty challenge gets the same message again.
        return new AmqpSaslInitialResponseClientExchange(_response, _response);
    }
}
