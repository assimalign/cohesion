using System;

using Assimalign.Cohesion.Database.Server;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The SQL model's wire-protocol server: fronts one <see cref="SqlDatabaseEngine"/>
/// on the network, deriving the model-agnostic machinery — accept loop, session
/// state machine, guardrails, two-phase drain — from the shared
/// <see cref="DatabaseServer"/> base.
/// </summary>
/// <remarks>
/// Servers are per-model: this type is where SQL-specific wire behavior grows
/// (typed relational payloads, SQL transaction frames) as the protocol's
/// model-specific surface lands. Today it adds the typed engine accessor and the
/// composition seam; execution rides the base's model-agnostic text-execute path.
/// Compose one with <see cref="Create"/>, or through the
/// <c>AddSqlServer(...)</c> builder verb.
/// </remarks>
public sealed class SqlDatabaseServer : DatabaseServer
{
    private SqlDatabaseServer(SqlDatabaseEngine engine, DatabaseServerOptions options)
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
    /// is inert until <see cref="IDatabaseServer.StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The SQL engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">The composition options. Requires a bound <see cref="DatabaseServerOptions.Listener"/>.</param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit.</exception>
    public static SqlDatabaseServer Create(SqlDatabaseEngine engine, DatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        return new SqlDatabaseServer(engine, options);
    }
}
