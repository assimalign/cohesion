using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel;

/// <summary>
/// Composition extensions for adding database manifests to an application model.
/// </summary>
public static class DatabaseResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Adds a manifest-backed Cohesion database resource to the application.
        /// </summary>
        /// <param name="manifest">The build-produced database resource manifest.</param>
        /// <param name="options">
        /// Optional deployer-owned replica and storage overrides.
        /// </param>
        /// <returns>The resource descriptor, for chaining dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="manifest"/> is <see langword="null"/>.
        /// </exception>
        public IApplicationResourceDescriptor AddDatabase(
            ResourceManifest manifest,
            DatabaseResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            return builder.AddResource(new DatabaseResource(manifest, options));
        }
    }
}
