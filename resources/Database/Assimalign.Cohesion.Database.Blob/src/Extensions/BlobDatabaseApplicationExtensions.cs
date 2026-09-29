using System;

using Assimalign.Cohesion.Database.Blob.Internal;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Registers deferred blob engines through the dependency-free Database application seam.</summary>
public static class BlobDatabaseApplicationExtensions
{
    extension(IDatabaseApplicationBuilder builder)
    {
        /// <summary>Registers a blob engine to construct when the application builds.</summary>
        /// <param name="configure">Receives the build context and engine options; invoked once during Build.</param>
        /// <returns>The application builder for further registration.</returns>
        /// <exception cref="ArgumentNullException">The builder or callback is null.</exception>
        /// <remarks>The application owns the created engine. Registration neither binds configuration nor resolves services.</remarks>
        public IDatabaseApplicationBuilder AddBlob(Action<IDatabaseApplicationContext, IBlobDatabaseEngineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            return builder.AddEngine(context =>
            {
                var engineBuilder = new BlobDatabaseEngineBuilder();
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
