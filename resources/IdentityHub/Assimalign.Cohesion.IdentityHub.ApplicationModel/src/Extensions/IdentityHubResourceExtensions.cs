using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.IdentityHub.ApplicationModel;

/// <summary>
/// Composition extensions for adding identity-hub manifests to an application model.
/// </summary>
public static class IdentityHubResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Adds a manifest-backed Cohesion identity-hub resource to the application.
        /// </summary>
        /// <param name="manifest">The build-produced identity-hub resource manifest.</param>
        /// <param name="options">Optional deployer-owned storage overrides.</param>
        /// <returns>The resource descriptor, for chaining dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="manifest"/> is <see langword="null"/>.
        /// </exception>
        public IApplicationResourceDescriptor AddIdentityHub(
            ResourceManifest manifest,
            IdentityHubResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            return builder.AddResource(new IdentityHubResource(manifest, options));
        }
    }
}
