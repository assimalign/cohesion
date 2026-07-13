using System;

using Assimalign.Cohesion.Database.Server;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Builder-time registration of the SQL model. The verbs ship with the model
/// package and compose against the area root's
/// <see cref="IDatabaseApplicationBuilder"/> seam only — no hosting reference —
/// so the SQL model can register its engine and server on any composition surface
/// that implements the builder (the cross-area builder pattern; the Web precedent
/// is <c>AddAuthentication</c> shipping in <c>Web.Authentication</c>).
/// </summary>
public static class SqlDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>
        /// Creates and registers a SQL database engine on the application as a
        /// server-less, embedded registration: file-backed when
        /// <see cref="SqlDatabaseEngineOptions.RootPath"/> is set, in-memory
        /// otherwise, with durability and worker cadence per the options. The
        /// engine is a data machine — operational (background workers running) as
        /// soon as this verb returns.
        /// </summary>
        /// <param name="configure">An optional callback to configure the engine options (storage path, durability, group-commit/checkpoint/write-back cadence).</param>
        /// <returns>
        /// The registered <see cref="SqlDatabaseEngine"/>, so the composition root
        /// can seed or provision databases — or front the engine with
        /// <c>AddSqlServer</c> — before the application starts (mirrors the Web
        /// convention of returning the feature's own composition object).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public SqlDatabaseEngine AddSqlDatabase(Action<SqlDatabaseEngineOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            SqlDatabaseEngineOptions options = new();
            configure?.Invoke(options);

            SqlDatabaseEngine engine = SqlDatabaseEngineFactory.Create(options);
            builder.AddEngine(engine);

            return engine;
        }

        /// <summary>
        /// Creates and registers a <see cref="SqlDatabaseServer"/> fronting the
        /// given SQL engine as one of the application's wire-protocol endpoints.
        /// Servers are per-model, so an application may add one server per model it
        /// serves.
        /// </summary>
        /// <param name="engine">The SQL engine the server fronts — typically the return of <c>AddSqlDatabase</c>, or an engine the composition root created itself. The composition root owns and disposes the engine.</param>
        /// <param name="configure">Configures the server options; must supply the bound <see cref="DatabaseServerOptions.Listener"/> (the composition root owns the listener).</param>
        /// <returns>
        /// The registered <see cref="SqlDatabaseServer"/> (the Web convention of
        /// returning the feature's own composition object).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/>, <paramref name="engine"/>, or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The configured options carry no listener or a non-positive session limit.</exception>
        public SqlDatabaseServer AddSqlServer(SqlDatabaseEngine engine, Action<DatabaseServerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(configure);

            DatabaseServerOptions options = new();
            configure.Invoke(options);

            SqlDatabaseServer server = SqlDatabaseServer.Create(engine, options);
            builder.AddServer(server);

            return server;
        }
    }
}
