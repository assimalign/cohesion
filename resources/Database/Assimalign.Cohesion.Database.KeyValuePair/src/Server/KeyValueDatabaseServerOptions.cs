using System;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Options controlling the key-value database server front-end: the bound
/// transport listener, the authenticator, and the DoS guardrails.
/// </summary>
/// <remarks>
/// The options deliberately carry no engine: servers are per-model and the
/// composition root supplies the single engine directly
/// (<see cref="KeyValueDatabaseServer.Create"/>, or the
/// <c>AddKeyValueServer(engine, configure)</c> builder verb).
/// </remarks>
public sealed class KeyValueDatabaseServerOptions
{
    /// <summary>
    /// Gets or sets the bound transport listener the server accepts connections
    /// from. The composition root composes the listener (TCP, named pipe,
    /// in-memory, …) and retains ownership — the server never disposes it.
    /// </summary>
    public IConnectionListener? Listener { get; set; }

    /// <summary>
    /// Gets or sets the authenticator consulted during the session handshake.
    /// When null the server uses <see cref="DatabaseAuthenticator.AllowAll"/> —
    /// the MVP development posture, which accepts every principal. Production
    /// deployments must supply a real implementation.
    /// </summary>
    public IDatabaseAuthenticator? Authenticator { get; set; }

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
    /// Gets or sets the drain budget honored by <see cref="IDatabaseServer.StopAsync"/>
    /// before remaining sessions are aborted.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
