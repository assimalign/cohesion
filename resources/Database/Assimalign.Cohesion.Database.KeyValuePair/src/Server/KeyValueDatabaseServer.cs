using System;

using Assimalign.Cohesion.Database.Server;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// The key-value model's wire-protocol server: fronts one
/// <see cref="KeyValueDatabaseEngine"/> on the network, deriving the accept
/// loop, session state machine and frame pump, guardrails, and two-phase drain
/// from the shared server core (<see cref="DatabaseServer"/>) — the area root's
/// <see cref="IDatabaseServer"/> contract, implemented per model.
/// </summary>
/// <remarks>
/// This is the <b>second model server</b> — the one whose construction fired the
/// area's recorded server-core extraction trigger (2026-07-14). It rides the
/// shared core unmodified: the key-value command grammar
/// (<c>docs/COMMANDS.md</c>) travels the protocol's existing Execute message
/// (statement text + named tuple-codec parameters) into the root's text-execute
/// seam, and the model's result sets ride the generic
/// ResultHeader/Row/Complete framing — zero protocol changes, zero
/// model-specific pump behavior. Model-specific wire surface (binary command
/// frames, if measurement ever demands them) would grow here. The server is
/// created inert; <c>StartAsync</c> begins accepting, <c>StopAsync</c> drains
/// within <see cref="DatabaseServerOptions.ShutdownDrainTimeout"/> then aborts,
/// and disposal stops the server. The composition root owns the listener and
/// the engine. Compose one with <see cref="Create"/>, or through the
/// <c>AddKeyValueServer(...)</c> builder verb.
/// </remarks>
public sealed class KeyValueDatabaseServer : DatabaseServer
{
    private KeyValueDatabaseServer(KeyValueDatabaseEngine engine, KeyValueDatabaseServerOptions options)
        : base(engine, options)
    {
        Engine = engine;
    }

    /// <summary>
    /// Gets the key-value engine this server fronts (the typed counterpart of
    /// <see cref="IDatabaseServerContext.Engine"/>).
    /// </summary>
    public KeyValueDatabaseEngine Engine { get; }

    /// <summary>
    /// Creates a key-value database server over the given engine and options. The
    /// server is inert until <see cref="DatabaseServer.StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The key-value engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">The composition options. Requires a bound <see cref="DatabaseServerOptions.Listener"/>.</param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit.</exception>
    public static KeyValueDatabaseServer Create(KeyValueDatabaseEngine engine, KeyValueDatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        return new KeyValueDatabaseServer(engine, options);
    }
}
