using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.EventHub.ApplicationModel;

/// <summary>Composition extensions for EventHub manifests.</summary>
public static class EventHubResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed EventHub resource.</summary>
        /// <param name="manifest">The build-produced EventHub manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public IEventHubResourceDescriptor AddEventHub(
            ResourceManifest manifest,
            EventHubResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new EventHubResourceDescriptor(builder.AddResource(new EventHubResource(manifest, options)));
        }
    }
}
