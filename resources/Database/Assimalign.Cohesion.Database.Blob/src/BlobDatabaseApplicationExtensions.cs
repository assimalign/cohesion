using System;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// Builder-time registration of the blob model. The verbs ship with the
/// model package and compose against the area root's
/// <see cref="IDatabaseApplicationBuilder"/> seam only — no hosting reference —
/// so the blob model can register its engine on any composition
/// surface that implements the builder (the cross-area builder pattern; the SQL
/// precedent is <c>AddSqlDatabase</c>/<c>AddSqlServer</c> shipping in
/// <c>Database.Sql</c>).
/// </summary>
public static class BlobDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>
        /// Creates and registers a blob database engine on the application as
        /// a server-less, embedded registration: file-backed when
        /// <see cref="BlobDatabaseEngineOptions.RootPath"/> is set, in-memory
        /// otherwise, with durability and worker cadence per the options. The
        /// engine is a data machine — operational (background workers running) as
        /// soon as this verb returns.
        /// </summary>
        /// <param name="configure">An optional callback to configure the engine options (storage path, durability, group-commit/checkpoint/write-back cadence).</param>
        /// <returns>
        /// The registered <see cref="BlobDatabaseEngine"/>, so the composition
        /// root can create databases before the application starts.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public BlobDatabaseEngine AddBlobDatabase(Action<BlobDatabaseEngineOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            BlobDatabaseEngineOptions options = new();
            configure?.Invoke(options);

            BlobDatabaseEngine engine = BlobDatabaseEngine.Create(options);
            builder.AddEngine(engine);

            return engine;
        }

    }
}


