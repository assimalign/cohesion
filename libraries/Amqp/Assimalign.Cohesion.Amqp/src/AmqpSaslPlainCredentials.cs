namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the credentials a client presented with the SASL <c>PLAIN</c> mechanism (RFC 4616).
/// </summary>
/// <remarks>
/// The type deliberately does not override <see cref="object.ToString"/>, so logging an instance never
/// writes the password.
/// </remarks>
public sealed class AmqpSaslPlainCredentials
{
    /// <summary>
    /// Initializes new <c>PLAIN</c> credentials.
    /// </summary>
    /// <param name="authorizationId">The identity the client asks to act as, or <see langword="null"/> to act as itself.</param>
    /// <param name="userName">The authentication identity.</param>
    /// <param name="password">The password.</param>
    public AmqpSaslPlainCredentials(string? authorizationId, string userName, string password)
    {
        AuthorizationId = authorizationId;
        UserName = userName;
        Password = password;
    }

    /// <summary>
    /// Gets the identity the client asks to act as, or <see langword="null"/> when it asks to act as
    /// <see cref="UserName"/>.
    /// </summary>
    public string? AuthorizationId { get; }

    /// <summary>
    /// Gets the authentication identity.
    /// </summary>
    public string UserName { get; }

    /// <summary>
    /// Gets the password.
    /// </summary>
    public string Password { get; }
}
