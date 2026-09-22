using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Composition extensions for MediaHub manifests.</summary>
public static class MediaHubResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed MediaHub resource.</summary>
        /// <param name="manifest">The build-produced MediaHub manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public IMediaHubResourceDescriptor AddMediaHub(
            ResourceManifest manifest,
            MediaHubResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new MediaHubResourceDescriptor(builder.AddResource(new MediaHubResource(manifest, options)));
        }
    }
}
