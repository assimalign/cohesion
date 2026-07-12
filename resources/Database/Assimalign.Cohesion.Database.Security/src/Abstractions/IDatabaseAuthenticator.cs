using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Security;

/// <summary>
/// Verifies a claimed principal during the wire-protocol authentication handshake.
/// </summary>
/// <remarks>
/// The server sends an authentication challenge after startup and passes the
/// client's response here together with the claimed principal and the database it
/// wants. Implementations decide what the evidence bytes mean (password, token,
/// certificate proof); the MVP default (<see cref="DatabaseAuthenticator.AllowAll"/>)
/// accepts every principal and is intended for development and trusted-transport
/// deployments only.
/// </remarks>
public interface IDatabaseAuthenticator
{
    /// <summary>
    /// Verifies an authentication attempt.
    /// </summary>
    /// <param name="database">The database the session wants to bind to.</param>
    /// <param name="principal">The principal name the client claims.</param>
    /// <param name="evidence">The client's authentication response bytes; empty for trust-based methods.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the principal is authenticated; otherwise false.</returns>
    ValueTask<bool> AuthenticateAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken = default);
}
