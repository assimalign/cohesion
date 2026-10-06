using System;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Registers deferred document engines through the dependency-free Database application seam.</summary>
public static class DocumentDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>Registers a document engine to construct when the application builds.</summary>
        /// <param name="configure">Receives the build context and the engine builder; invoked once during Build.</param>
        /// <returns>The application builder for further registration.</returns>
        /// <exception cref="ArgumentNullException">The builder or callback is null.</exception>
        /// <remarks>
        /// The application owns the created engine. Registration neither binds configuration nor
        /// resolves services. A server factory registered on the builder receives the typed
        /// <see cref="DocumentDatabaseEngine"/>, so it needs no cast.
        /// </remarks>
        public IDatabaseApplicationBuilder AddDocuments(Action<IDatabaseApplicationContext, DocumentDatabaseEngineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            return builder.AddEngine(context =>
            {
                var engineBuilder = new DocumentDatabaseEngineBuilder();
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
