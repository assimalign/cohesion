using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Composition extensions for IoTHub manifests.</summary>
public static class IoTHubResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed IoTHub resource.</summary>
        /// <param name="manifest">The build-produced IoTHub manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public IIoTHubResourceDescriptor AddIoTHub(
            ResourceManifest manifest,
            IoTHubResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new IoTHubResourceDescriptor(builder.AddResource(new IoTHubResource(manifest, options)));
        }
    }
}
