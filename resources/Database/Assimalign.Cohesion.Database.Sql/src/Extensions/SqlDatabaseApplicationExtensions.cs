using System;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Registers deferred SQL composition through the dependency-free Database root contract.</summary>
public static class SqlDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>Registers SQL engine intent; construction occurs during application Build.</summary>
        /// <param name="configure">Configures model options and nested factories against the build-time context.</param>
        /// <returns>The application builder.</returns>
        /// <exception cref="ArgumentNullException">The builder or callback is null.</exception>
        public IDatabaseApplicationBuilder AddSql(Action<IDatabaseApplicationContext, ISqlDatabaseEngineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            return builder.AddEngine(context =>
            {
                var engineBuilder = new SqlDatabaseEngineBuilder();
                try
                {
                    configure(context, engineBuilder);
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