using System;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Registers deferred graph engines through the dependency-free Database application seam.</summary>
public static class GraphDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>
        /// Registers a graph engine of that name; the name is reserved now, and the engine is
        /// constructed when the application builds.
        /// </summary>
        /// <param name="name">The engine name, written once (owner decision 52 of 2026-10-09).</param>
        /// <param name="configure">Receives the engine builder; invoked once during Build.</param>
        /// <returns>The application builder for further registration.</returns>
        /// <exception cref="ArgumentNullException">The builder, the name or the callback is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
        /// <exception cref="InvalidOperationException">
        /// Registration is closed, or another engine of the application has the same name.
        /// </exception>
        /// <remarks>
        /// The application owns the created engine. Registration neither binds configuration nor
        /// resolves services: a closure over the application's configuration reads it when the
        /// callback runs. A server factory registered on the builder receives the typed
        /// <see cref="GraphDatabaseEngine"/>, so it needs no cast.
        /// </remarks>
        public IDatabaseApplicationBuilder AddGraph(string name, Action<GraphDatabaseEngineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(configure);
            return builder.AddEngine(name, _ =>
            {
                var engineBuilder = GraphDatabaseEngine.CreateBuilder(name);
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
