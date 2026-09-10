using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel;

/// <summary>
/// Composition extensions for adding configuration-store manifests to an application model.
/// </summary>
public static class ConfigurationStoreResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Adds a manifest-backed Cohesion configuration-store resource to the application.
        /// </summary>
        /// <param name="manifest">The build-produced configuration-store resource manifest.</param>
        /// <param name="options">
        /// Optional deployer-owned replica and storage overrides.
        /// </param>
        /// <returns>The resource descriptor, for chaining dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="manifest"/> is <see langword="null"/>.
        /// </exception>
        public IApplicationResourceDescriptor AddConfigurationStore(
            ResourceManifest manifest,
            ConfigurationStoreResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            return builder.AddResource(new ConfigurationStoreResource(manifest, options));
        }
    }
}
