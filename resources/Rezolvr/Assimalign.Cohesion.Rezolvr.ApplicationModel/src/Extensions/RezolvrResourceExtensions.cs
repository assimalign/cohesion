using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Rezolvr.ApplicationModel;

/// <summary>Composition extensions for Rezolvr manifests.</summary>
public static class RezolvrResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed Rezolvr resource.</summary>
        /// <param name="manifest">The build-produced Rezolvr manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public IRezolvrResourceDescriptor AddRezolvr(
            ResourceManifest manifest,
            RezolvrResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new RezolvrResourceDescriptor(builder.AddResource(new RezolvrResource(manifest, options)));
        }
    }
}
