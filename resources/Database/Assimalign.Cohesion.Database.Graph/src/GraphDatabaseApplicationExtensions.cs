using System;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// Builder-time registration of the graph model. The verbs ship with the
/// model package and compose against the area root's
/// <see cref="IDatabaseApplicationBuilder"/> seam only — no hosting reference —
/// so the graph model can register its engine on any composition
/// surface that implements the builder (the cross-area builder pattern; the SQL
/// precedent is <c>AddSqlDatabase</c>/<c>AddSqlServer</c> shipping in
/// <c>Database.Sql</c>).
/// </summary>
public static class GraphDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>
        /// Creates and registers a graph database engine on the application as
        /// a server-less, embedded registration: file-backed when
        /// <see cref="GraphDatabaseEngineOptions.RootPath"/> is set, in-memory
        /// otherwise, with durability and worker cadence per the options. The
        /// engine is a data machine — operational (background workers running) as
        /// soon as this verb returns.
        /// </summary>
        /// <param name="configure">An optional callback to configure the engine options (storage path, durability, group-commit/checkpoint/write-back cadence).</param>
        /// <returns>
        /// The registered <see cref="GraphDatabaseEngine"/>, so the composition
        /// root can create databases before the application starts.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public GraphDatabaseEngine AddGraphDatabase(Action<GraphDatabaseEngineOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            GraphDatabaseEngineOptions options = new();
            configure?.Invoke(options);

            GraphDatabaseEngine engine = GraphDatabaseEngine.Create(options);
            try
            {
                builder.AddEngine(engine);
                return engine;
            }
            catch
            {
                engine.Dispose();
                throw;
            }
        }

    }
}


