using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Builder-time registration of the key-value model. The verbs ship with the
/// model package and compose against the area root's
/// <see cref="IDatabaseApplicationBuilder"/> seam only — no hosting reference —
/// so the key-value model can register its engine and server on any composition
/// surface that implements the builder (the cross-area builder pattern; the SQL
/// precedent is <c>AddSqlDatabase</c>/<c>AddSqlServer</c> shipping in
/// <c>Database.Sql</c>).
/// </summary>
public static class KeyValueDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>
        /// Creates and registers a key-value database engine on the application as
        /// a server-less, embedded registration: file-backed when
        /// <see cref="KeyValueDatabaseEngineOptions.RootPath"/> is set, in-memory
        /// otherwise, with durability and worker cadence per the options. The
        /// engine is a data machine — operational (background workers running) as
        /// soon as this verb returns.
        /// </summary>
        /// <param name="configure">An optional callback to configure the engine options (storage path, durability, group-commit/checkpoint/write-back cadence).</param>
        /// <returns>
        /// The registered <see cref="KeyValueDatabaseEngine"/>, so the composition
        /// root can seed or provision databases — or front the engine with
        /// <c>AddKeyValueServer</c> — before the application starts (the convention
        /// of returning the feature's own composition object).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public KeyValueDatabaseEngine AddKeyValueDatabase(Action<KeyValueDatabaseEngineOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            KeyValueDatabaseEngineOptions options = new();
            configure?.Invoke(options);

            KeyValueDatabaseEngine engine = KeyValueDatabaseEngine.Create(options);
            builder.AddEngine(engine);

            return engine;
        }

        /// <summary>
        /// Creates and registers a <see cref="KeyValueDatabaseServer"/> fronting the
        /// given key-value engine as one of the application's wire-protocol
        /// endpoints. Servers are per-model, so an application may add one server
        /// per model it serves.
        /// </summary>
        /// <param name="engine">The key-value engine the server fronts — typically the return of <c>AddKeyValueDatabase</c>, or an engine the composition root created itself. The composition root owns and disposes the engine.</param>
        /// <param name="configure">Configures the server options; must supply the bound <see cref="KeyValueDatabaseServerOptions.Listener"/> (the composition root owns the listener).</param>
        /// <returns>
        /// The registered <see cref="KeyValueDatabaseServer"/> (the convention of
        /// returning the feature's own composition object).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/>, <paramref name="engine"/>, or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The configured options carry no listener or a non-positive session limit.</exception>
        public KeyValueDatabaseServer AddKeyValueServer(KeyValueDatabaseEngine engine, Action<KeyValueDatabaseServerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(configure);

            KeyValueDatabaseServerOptions options = new();
            configure.Invoke(options);

            KeyValueDatabaseServer server = KeyValueDatabaseServer.Create(engine, options);
            builder.AddServer(server);

            return server;
        }
    }
}
