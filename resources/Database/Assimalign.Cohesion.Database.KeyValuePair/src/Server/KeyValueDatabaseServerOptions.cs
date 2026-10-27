using System;


namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Options controlling the key-value database server front-end: the
/// transport listener, the authenticator, and the DoS guardrails.
/// </summary>
/// <remarks>
/// The options deliberately carry no engine: servers are per-model and the
/// composition root supplies the single engine directly
/// (<see cref="KeyValueDatabaseServer.Create"/>, or the
/// <c>engineBuilder.AddServer(configure)</c> and <c>engineBuilder.AddServer(factory)</c> builder verbs).
/// </remarks>
public sealed class KeyValueDatabaseServerOptions : DatabaseServerOptions
{
    /// <summary>
    /// Gets or sets the peer-close wait after terminal output completes. Defaults to two seconds.
    /// Must be positive, finite, and at most 4,294,967,294 milliseconds.
    /// The shutdown drain budget remains the outer limit on all terminal I/O.
    /// </summary>
    public TimeSpan TerminalDrainLinger { get; set; } = TimeSpan.FromSeconds(2);

    internal void ValidateFor(DatabaseName engineName)
    {
        ValidateCommon("KeyValuePair", engineName);
        ValidateTimeout(ShutdownDrainTimeout, nameof(ShutdownDrainTimeout), "KeyValuePair", engineName);
        ValidateTimeout(TerminalDrainLinger, nameof(TerminalDrainLinger), "KeyValuePair", engineName);
    }

    /// <summary>
    /// Copies every option, so a server keeps settings its caller can no longer change: a later
    /// change to the caller's object (one an <c>AddServer(configure)</c> callback captured, say)
    /// never reaches a running server or bypasses the checks it was created with.
    /// </summary>
    /// <returns>A copy of these options; the listener and the authenticator are the same instances.</returns>
    internal KeyValueDatabaseServerOptions Snapshot() => new()
    {
        Listener = Listener,
        Authenticator = Authenticator,
        MaxSessions = MaxSessions,
        AuthenticationTimeout = AuthenticationTimeout,
        IdleTimeout = IdleTimeout,
        ShutdownDrainTimeout = ShutdownDrainTimeout,
        TerminalDrainLinger = TerminalDrainLinger,
    };
}
