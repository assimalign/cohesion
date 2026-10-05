using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Security.Internal;

namespace Assimalign.Cohesion.Database.Security;

/// <summary>
/// Verifies a claimed principal during the wire-protocol authentication handshake.
/// </summary>
/// <remarks>
/// <para>
/// The server sends an authentication challenge after startup and passes the client's response
/// here together with the claimed principal and the database it wants. A derived authenticator
/// decides what the evidence bytes mean (password, token, certificate proof) in
/// <see cref="AuthenticateCoreAsync(string, string, ReadOnlyMemory{byte}, CancellationToken)"/>.
/// </para>
/// <para>
/// The model servers call it and the application supplies it through the server options, so the
/// constructor is protected. The built-in <see cref="AllowAll"/> accepts every principal and is
/// intended for development and trusted-transport deployments only.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseAuthenticator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseAuthenticator"/> class.
    /// </summary>
    protected DatabaseAuthenticator()
    {
    }

    /// <summary>
    /// Gets an authenticator that accepts every principal without verifying any
    /// evidence — the MVP development posture. Production deployments supply a
    /// real implementation through the server options.
    /// </summary>
    public static DatabaseAuthenticator AllowAll { get; } = new AllowAllDatabaseAuthenticator();

    /// <summary>
    /// Verifies an authentication attempt.
    /// </summary>
    /// <param name="database">The database the session wants to bind to.</param>
    /// <param name="principal">The principal name the client claims.</param>
    /// <param name="evidence">The client's authentication response bytes; empty for trust-based methods.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the principal is authenticated; otherwise false.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="database"/> or <paramref name="principal"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is already canceled; the core is not called.</exception>
    public ValueTask<bool> AuthenticateAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        return AuthenticateCoreAsync(database, principal, evidence, cancellationToken);
    }

    /// <summary>
    /// Verifies an authentication attempt whose arguments <see cref="AuthenticateAsync"/> has
    /// checked.
    /// </summary>
    /// <param name="database">The database the session wants to bind to; never null.</param>
    /// <param name="principal">The principal name the client claims; never null.</param>
    /// <param name="evidence">The client's authentication response bytes; empty for trust-based methods.</param>
    /// <param name="cancellationToken">Cancellation token for the operation; not canceled when the call starts.</param>
    /// <returns>True when the principal is authenticated; otherwise false.</returns>
    /// <remarks>
    /// Return false for a failed attempt; the server maps it to the wire's
    /// <c>AuthenticationFailed</c> error. Throw only for an infrastructure failure.
    /// </remarks>
    protected abstract ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken);
}
