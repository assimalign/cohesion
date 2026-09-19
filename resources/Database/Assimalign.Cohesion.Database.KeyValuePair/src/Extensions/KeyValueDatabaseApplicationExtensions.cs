using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>Registers deferred key-value composition through the dependency-free Database root contract.</summary>
public static class KeyValueDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>Registers key-value engine intent; construction occurs during application Build.</summary>
        /// <param name="configure">Configures model options and nested factories against the build-time context.</param>
        /// <returns>The application builder.</returns>
        /// <exception cref="ArgumentNullException">The builder or callback is null.</exception>
        public IDatabaseApplicationBuilder AddKeyValue(Action<IDatabaseApplicationContext, IKeyValueDatabaseEngineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            return builder.AddEngine(context =>
            {
                var engineBuilder = new KeyValueDatabaseEngineBuilder();
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