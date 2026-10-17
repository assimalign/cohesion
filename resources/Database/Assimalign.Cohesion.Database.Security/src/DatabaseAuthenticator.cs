using System;
using System.Runtime.CompilerServices;
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
    /// <remarks>
    /// While a listener takes the <c>Assimalign.Cohesion.Database.Security</c> event source, the
    /// verdict, or the core's failure, is written to it with the database and the principal; the
    /// evidence never is. A core that throws is not caught: the exception reaches the caller unchanged.
    /// </remarks>
    public ValueTask<bool> AuthenticateAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();

        // Disabled: the core's task is returned as it is, so the trace costs one check.
        if (!DatabaseSecurityEventSource.Log.IsVerdictTraceEnabled())
        {
            return AuthenticateCoreAsync(database, principal, evidence, cancellationToken);
        }

        ValueTask<bool> pending;
        Exception? thrown = null;
        try
        {
            pending = AuthenticateCoreAsync(database, principal, evidence, cancellationToken);
        }
        catch (Exception exception) when (DatabaseSecurityEventSource.CaptureFailure(exception, out thrown))
        {
            // Unreachable: the filter records the failure and declines it, so it propagates from
            // the core unchanged.
            throw;
        }
        finally
        {
            // Written once the core's own finally blocks released what they held: a core that
            // throws synchronously may do so inside its own lock.
            if (thrown is not null)
            {
                ReportFailure(database, principal, thrown);
            }
        }

        if (pending.IsCompletedSuccessfully)
        {
            bool authenticated = pending.Result;
            DatabaseSecurityEventSource.Log.AuthenticationCompleted(this, database, principal, authenticated);
            return new ValueTask<bool>(authenticated);
        }

        return AuthenticateTracedAsync(pending, database, principal);
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

    /// <summary>
    /// Awaits a verdict that did not complete synchronously and writes it, or the core's failure, to
    /// the event source. Entered only while a listener takes the source.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> AuthenticateTracedAsync(ValueTask<bool> pending, string database, string principal)
    {
        bool authenticated;
        Exception? thrown = null;
        try
        {
            authenticated = await pending.ConfigureAwait(false);
        }
        catch (Exception exception) when (DatabaseSecurityEventSource.CaptureFailure(exception, out thrown))
        {
            // Unreachable: the filter records the failure and declines it.
            throw;
        }
        finally
        {
            if (thrown is not null)
            {
                ReportFailure(database, principal, thrown);
            }
        }

        DatabaseSecurityEventSource.Log.AuthenticationCompleted(this, database, principal, authenticated);
        return authenticated;
    }

    /// <summary>
    /// Writes a failure of the core to the event source. A cancellation is not a failure and is not
    /// written.
    /// </summary>
    private void ReportFailure(string database, string principal, Exception exception)
    {
        if (exception is not OperationCanceledException)
        {
            DatabaseSecurityEventSource.Log.AuthenticationFailed(this, database, principal, exception);
        }
    }
}
