using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>Registers deferred key-value composition through the dependency-free Database root contract.</summary>
public static class KeyValueDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>
        /// Registers a key-value engine of that name: the name is reserved now, and the engine is built,
        /// with its declared databases opened or created, during application Build.
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
        /// the factory creates <see cref="KeyValueDatabaseEngine.CreateBuilder(string)"/>, runs
        /// <paramref name="configure"/> and builds, which opens or creates the declared databases
        /// before application Build continues. The application owns the engine. Registration neither
        /// binds configuration nor resolves services, and a worker or server factory registered on
        /// the engine builder receives the typed <see cref="KeyValueDatabaseEngine"/>, so it needs no cast.
        /// <para>
        /// The open, and the recovery, of each declared database therefore run inside application
        /// Build, synchronously, with no timeout (a host's startup timeout bounds service start only)
        /// and no cancellation (owner decision 49 of 2026-10-09). A failure, such as a declared
        /// database whose files cannot be read, propagates out of application Build after the engines
        /// built before it are disposed.
        /// </para>
        /// </remarks>
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
