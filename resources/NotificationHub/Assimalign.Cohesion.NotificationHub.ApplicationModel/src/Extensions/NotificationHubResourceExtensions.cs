using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Composition extensions for NotificationHub manifests.</summary>
public static class NotificationHubResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed NotificationHub resource.</summary>
        /// <param name="manifest">The build-produced NotificationHub manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public INotificationHubResourceDescriptor AddNotificationHub(
            ResourceManifest manifest,
            NotificationHubResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new NotificationHubResourceDescriptor(builder.AddResource(new NotificationHubResource(manifest, options)));
        }
    }
}
