using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.EmailHub.ApplicationModel;

/// <summary>Composition extensions for EmailHub manifests.</summary>
public static class EmailHubResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed EmailHub resource.</summary>
        /// <param name="manifest">The build-produced EmailHub manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public IEmailHubResourceDescriptor AddEmailHub(
            ResourceManifest manifest,
            EmailHubResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new EmailHubResourceDescriptor(builder.AddResource(new EmailHubResource(manifest, options)));
        }
    }
}
