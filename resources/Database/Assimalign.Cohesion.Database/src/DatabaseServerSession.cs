using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of every authenticated client session on a server: the binding between a network
/// connection, an authenticated principal and an engine <see cref="DatabaseSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// A server session exists from the moment its server accepts the connection, before the
/// handshake. Its <see cref="Id"/> is fixed then; the protocol version is negotiated during the
/// handshake (<see cref="SetNegotiatedVersion"/>) and the principal becomes known once
/// authentication succeeds (<see cref="SetAuthenticatedPrincipal"/>), each set once through a
/// protected, non-virtual method. The engine session is the leaf's
/// (<see cref="DatabaseSession"/>, the one abstract public member, which a leaf may override
/// covariantly).
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 3, #1259).</b> The plan's constructor took the version and
/// principal too; neither is known when the server constructs a session, so the base takes them
/// when they are. The leaves live in the model assemblies and stay internal sealed, so the
/// constructor is <c>protected</c>.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseServerSession : IAsyncDisposable
{
    private readonly Guid _id = Guid.NewGuid();
    private readonly object _sync = new();
    private ProtocolVersion _protocolVersion;
    private bool _negotiated;
    private string? _principal;

    /// <summary>
    /// Initializes a new server session with a new unique identifier, before its handshake.
    /// </summary>
    protected DatabaseServerSession()
    {
    }

    /// <summary>
    /// Gets the unique identifier of this server session.
    /// </summary>
    public Guid Id => _id;

    /// <summary>
    /// Gets the protocol version negotiated with the client, or the default value before the
    /// handshake negotiated one.
    /// </summary>
    public ProtocolVersion ProtocolVersion
    {
        get
        {
            lock (_sync)
            {
                return _protocolVersion;
            }
        }
    }

    /// <summary>
    /// Gets the name of the authenticated principal, or null while authentication is still in
    /// progress.
    /// </summary>
    public string? Principal => Volatile.Read(ref _principal);

    /// <summary>
    /// Gets the engine session this server session executes against, or null before startup
    /// completes.
    /// </summary>
    public abstract DatabaseSession? DatabaseSession { get; }

    /// <summary>
    /// Tears the session down: the leaf aborts its connection and waits for its pump to wind down.
    /// </summary>
    /// <returns>A task that completes once the session has wound down.</returns>
    public ValueTask DisposeAsync() => DisposeAsyncCore();

    /// <summary>
    /// Records the protocol version the handshake negotiated.
    /// </summary>
    /// <param name="version">The negotiated version.</param>
    /// <exception cref="InvalidOperationException">A version was already negotiated.</exception>
    protected void SetNegotiatedVersion(ProtocolVersion version)
    {
        lock (_sync)
        {
            if (_negotiated)
            {
                throw new InvalidOperationException("The session's protocol version was already negotiated.");
            }

            _protocolVersion = version;
            _negotiated = true;
        }

        DatabaseEventSource.Log.ServerSessionNegotiated(this, version);
    }

    /// <summary>
    /// Records the principal authentication accepted.
    /// </summary>
    /// <param name="principal">The authenticated principal's name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A principal was already authenticated.</exception>
    protected void SetAuthenticatedPrincipal(string principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (Interlocked.CompareExchange(ref _principal, principal, null) is not null)
        {
            throw new InvalidOperationException("The session's principal was already authenticated.");
        }

        DatabaseEventSource.Log.ServerSessionAuthenticated(this, principal);
    }

    /// <summary>
    /// Aborts the leaf's connection and waits for its pump to wind down. Must be idempotent: the
    /// server's drain and the session's own disposal may both call it.
    /// </summary>
    /// <returns>A task that completes once the session has wound down.</returns>
    protected abstract ValueTask DisposeAsyncCore();
}
