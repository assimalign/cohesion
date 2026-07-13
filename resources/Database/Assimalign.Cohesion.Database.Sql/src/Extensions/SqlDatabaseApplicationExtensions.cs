using System;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Builder-time registration of the SQL model. The verb ships with the model
/// package and composes against the area root's
/// <see cref="IDatabaseApplicationBuilder"/> seam only — no hosting reference —
/// so the SQL model can register its engine on any composition surface that
/// implements the builder (the cross-area builder pattern; the Web precedent is
/// <c>AddAuthentication</c> shipping in <c>Web.Authentication</c>).
/// </summary>
public static class SqlDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>
        /// Registers a SQL database engine on the application: file-backed when
        /// <see cref="SqlDatabaseEngineOptions.RootPath"/> is set, in-memory
        /// otherwise, with durability and worker cadence per the options.
        /// </summary>
        /// <param name="configure">An optional callback to configure the engine options (storage path, durability, group-commit/checkpoint/write-back cadence).</param>
        /// <returns>
        /// The registered <see cref="SqlDatabaseEngine"/>, so the composition root
        /// can seed or provision databases before the application starts (mirrors
        /// the Web convention of returning the feature's own composition object).
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
    }
}
