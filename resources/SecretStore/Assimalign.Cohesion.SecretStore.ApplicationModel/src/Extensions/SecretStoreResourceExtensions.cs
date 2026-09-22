using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Composition extensions for adding secret-store manifests to an application model.
/// </summary>
public static class SecretStoreResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Adds a manifest-backed Cohesion secret-store resource to the application.
        /// </summary>
        /// <param name="manifest">The build-produced secret-store resource manifest.</param>
        /// <param name="options">
        /// Optional deployer-owned planning overrides. The effective replica count must
        /// remain one until replication is supported.
        /// </param>
        /// <returns>
        /// The typed secret-store resource descriptor, for chaining dependency edges.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="manifest"/> is <see langword="null"/>.
        /// </exception>
        public ISecretStoreResourceDescriptor AddSecretStore(
            ResourceManifest manifest,
            SecretStoreResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            IApplicationResourceDescriptor descriptor = builder.AddResource(
                new SecretStoreResource(manifest, options));
            return new SecretStoreResourceDescriptor(descriptor);
        }
    }
}
