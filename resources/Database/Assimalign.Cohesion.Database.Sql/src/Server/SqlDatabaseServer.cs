using System;

using Assimalign.Cohesion.Database.Server;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The SQL model's wire-protocol server: fronts one <see cref="SqlDatabaseEngine"/>
/// on the network, deriving the accept loop, session state machine and frame
/// pump, guardrails, and two-phase drain from the shared server core
/// (<see cref="DatabaseServer"/>) — the area root's <see cref="IDatabaseServer"/>
/// contract, implemented per model.
/// </summary>
/// <remarks>
/// Servers are per-model, and the root contract is the only area-wide
/// requirement. The server machinery lived inside this package until the second
/// model server (<c>KeyValueDatabaseServer</c>) fired the recorded extraction
/// trigger on 2026-07-14 — the proven common core now lives in
/// <c>Assimalign.Cohesion.Database.Server</c> (see its docs/DESIGN.md for the
/// prediction-vs-evidence record), and this type is where SQL-specific wire
/// behavior grows (typed relational payloads, SQL transaction frames) as the
/// protocol's model-specific surface lands; today execution rides the
/// model-agnostic text-execute seam on the root's <c>IDatabaseSession</c>. The
/// server is created inert; <c>StartAsync</c> begins accepting, <c>StopAsync</c>
/// drains within <see cref="DatabaseServerOptions.ShutdownDrainTimeout"/> then
/// aborts, and disposal stops the server. The composition root owns the listener
/// and the engine. Compose one with <see cref="Create"/>, or through the
/// <c>AddSqlServer(...)</c> builder verb.
/// </remarks>
public sealed class SqlDatabaseServer : DatabaseServer
{
    private SqlDatabaseServer(SqlDatabaseEngine engine, SqlDatabaseServerOptions options)
        : base(engine, options)
    {
        Engine = engine;
    }

    /// <summary>
    /// Gets the SQL engine this server fronts (the typed counterpart of
    /// <see cref="IDatabaseServerContext.Engine"/>).
    /// </summary>
    public SqlDatabaseEngine Engine { get; }

    /// <summary>
    /// Creates a SQL database server over the given engine and options. The server
    /// is inert until <see cref="DatabaseServer.StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The SQL engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">The composition options. Requires a bound <see cref="DatabaseServerOptions.Listener"/>.</param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit.</exception>
    public static SqlDatabaseServer Create(SqlDatabaseEngine engine, SqlDatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        return new SqlDatabaseServer(engine, options);
    }
}
