using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

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
        public IConfigurationStoreResourceDescriptor AddConfigurationStore(
            ResourceManifest manifest,
            ConfigurationStoreResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            return new ConfigurationStoreResourceDescriptor(builder.AddResource(new ConfigurationStoreResource(manifest, options)));
        }

        /// <summary>Binds a manifest-backed remote ConfigurationStore resource with its typed command surface.</summary>
        /// <param name="declaration">The build-produced external declaration.</param>
        /// <param name="configure">The peer gateway, file, endpoint, or contributed binding.</param>
        /// <returns>The typed external graph descriptor.</returns>
        /// <exception cref="ArgumentNullException">The builder, declaration, or configuration callback is null.</exception>
        /// <exception cref="ArgumentException">The declaration has no ConfigurationStore manifest.</exception>
        /// <exception cref="InvalidOperationException">The external conflicts with an existing declaration.</exception>
        public IConfigurationStoreResourceDescriptor RemoteReferenceConfigurationStore(
            ExternalResourceDeclaration declaration,
            Action<RemoteReferenceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(declaration);
            if (!string.Equals(declaration.Manifest?.Kind, "ConfigurationStore", StringComparison.Ordinal))
            {
                throw new ArgumentException("A typed ConfigurationStore reference requires an ConfigurationStore manifest.", nameof(declaration));
            }
            return new ConfigurationStoreResourceDescriptor(builder.RemoteReference(declaration, configure));
        }
    }
}
