using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>Registers deferred key-value composition through the dependency-free Database root contract.</summary>
public static class KeyValueDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>
        /// Registers a key-value engine of that name; the name is reserved now, and the engine is
        /// constructed during application Build.
        /// </summary>
        /// <param name="name">The engine name, written once (owner decision 52 of 2026-10-09).</param>
        /// <param name="configure">Configures model options and nested factories; invoked once during Build.</param>
        /// <returns>The application builder.</returns>
        /// <exception cref="ArgumentNullException">The builder, the name or the callback is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
        /// <exception cref="InvalidOperationException">
        /// Registration is closed, or another engine of the application has the same name.
        /// </exception>
        public IDatabaseApplicationBuilder AddKeyValue(string name, Action<KeyValueDatabaseEngineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(configure);
            return builder.AddEngine(name, _ =>
            {
                var engineBuilder = KeyValueDatabaseEngine.CreateBuilder(name);
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
