using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.MessageHub.ApplicationModel;

/// <summary>Composition extensions for MessageHub manifests.</summary>
public static class MessageHubResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed MessageHub resource.</summary>
        /// <param name="manifest">The build-produced MessageHub manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public IMessageHubResourceDescriptor AddMessageHub(
            ResourceManifest manifest,
            MessageHubResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new MessageHubResourceDescriptor(builder.AddResource(new MessageHubResource(manifest, options)));
        }
    }
}
