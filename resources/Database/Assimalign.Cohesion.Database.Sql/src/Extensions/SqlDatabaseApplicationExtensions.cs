using System;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Registers deferred SQL composition through the dependency-free Database root contract.</summary>
public static class SqlDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>
        /// Registers a SQL engine of that name: the name is reserved now, and the engine is built,
        /// with its declared databases provisioned, during application Build.
        /// </summary>
        /// <param name="name">The engine name, written once (owner decision 52 of 2026-10-09).</param>
        /// <param name="configure">
        /// Configures the engine builder (options, declared databases, servers and workers); invoked
        /// once, during application Build, so a closure over the application's configuration reads
        /// its final values.
        /// </param>
        /// <returns>The application builder.</returns>
        /// <exception cref="ArgumentNullException">The builder, the name or the callback is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
        /// <exception cref="InvalidOperationException">
        /// Registration is closed, or another engine of the application has the same name; refused
        /// here, before any factory runs.
        /// </exception>
        /// <remarks>
        /// A shim over the root seam's named
        /// <see cref="IDatabaseApplicationBuilder.AddEngine(string, Func{IDatabaseApplicationContext, DatabaseEngine})"/>:
        /// the factory creates <see cref="SqlDatabaseEngine.CreateBuilder(string)"/>, runs
        /// <paramref name="configure"/> and builds, which provisions the declared databases before
        /// application Build continues. The callback receives no application context: what it may
        /// want from one, configuration and environment, is in scope in <c>Program.cs</c>, and an
        /// engine that needs the container is registered with the hosting builder's own
        /// <c>AddEngine(name, context =&gt; …)</c>.
        /// <para>
        /// The engine's open, recovery and schema migration therefore run inside application Build,
        /// synchronously, with no timeout (a host's startup timeout bounds service start only) and no
        /// cancellation (owner decision 49 of 2026-10-09). A provisioning failure, for example a
        /// <see cref="Assimalign.Cohesion.Database.Sql.Schema.SqlSchemaMigrationException"/> led by <c>COHSQLP001</c> to
        /// <c>COHSQLP005</c>, propagates out of application Build after the engines built before it
        /// are disposed.
        /// </para>
        /// </remarks>
        public IDatabaseApplicationBuilder AddSql(string name, Action<SqlDatabaseEngineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(configure);
            return builder.AddEngine(name, _ =>
            {
                SqlDatabaseEngineBuilder engineBuilder = SqlDatabaseEngine.CreateBuilder(name);
                try
                {
                    configure(engineBuilder);
                    return engineBuilder.Build();
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    engineBuilder.Abort(failure);
                    throw;
                }
            });
        }
    }
}
