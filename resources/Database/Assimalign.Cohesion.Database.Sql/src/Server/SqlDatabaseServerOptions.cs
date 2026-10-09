using System;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Options controlling the SQL database server front-end: the transport
/// listener, the authenticator, and the DoS guardrails.
/// </summary>
/// <remarks>
/// The options deliberately carry no engine: servers are per-model and the
/// composition root supplies the single engine directly
/// (<see cref="SqlDatabaseServer.Create"/>, or the <c>engineBuilder.AddServer(factory)</c>
/// builder verb).
/// </remarks>
public sealed class SqlDatabaseServerOptions
{
    /// <summary>
    /// Gets or sets the transport listener the server binds and accepts
    /// connections from. The composition root configures the listener (TCP,
    /// named pipe, in-memory, …), then transfers its lifecycle to the server.
    /// Stop terminally disposes the listener.
    /// </summary>
    public IConnectionListener? Listener { get; set; }

    /// <summary>
    /// Gets or sets the authenticator consulted during the session handshake.
    /// When null the server uses <see cref="DatabaseAuthenticator.AllowAll"/> —
    /// the MVP development posture, which accepts every principal. Production
    /// deployments must supply a real implementation.
    /// </summary>
    public DatabaseAuthenticator? Authenticator { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent sessions the server accepts.
    /// Connections beyond the limit are rejected with an unavailable error.
    /// </summary>
    public int MaxSessions { get; set; } = 100;

    /// <summary>
    /// Gets or sets the time an unauthenticated connection may hold a slot before
    /// it is dropped.
    /// </summary>
    public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the idle time after which a session is closed. Set to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable idle eviction.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets the drain budget honored by <see cref="DatabaseServer.StopAsync"/>
    /// before remaining sessions are aborted.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Copies every option, so a server keeps settings its caller can no longer change: a later
    /// change to the caller's object (one an <c>AddServer(configure)</c> callback captured, say)
    /// never reaches a running server or bypasses the checks it was created with.
    /// </summary>
    /// <returns>A copy of these options; the listener and the authenticator are the same instances.</returns>
    internal SqlDatabaseServerOptions Snapshot() => new()
    {
        Listener = Listener,
        Authenticator = Authenticator,
        MaxSessions = MaxSessions,
        AuthenticationTimeout = AuthenticationTimeout,
        IdleTimeout = IdleTimeout,
        ShutdownDrainTimeout = ShutdownDrainTimeout,
    };
}
