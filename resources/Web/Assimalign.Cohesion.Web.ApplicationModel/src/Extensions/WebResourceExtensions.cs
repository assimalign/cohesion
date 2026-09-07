using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Web.ApplicationModel;

/// <summary>
/// Composition extensions for adding Web manifests to an application model.
/// </summary>
public static class WebResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Adds a manifest-backed Cohesion Web resource to the application.
        /// </summary>
        /// <param name="manifest">The build-produced Web resource manifest.</param>
        /// <param name="options">
        /// Optional platform-neutral replica overrides.
        /// </param>
        /// <returns>The resource descriptor, for chaining dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="manifest"/> is <see langword="null"/>.
        /// </exception>
        public IApplicationResourceDescriptor AddWeb(
            ResourceManifest manifest,
            WebResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            return builder.AddResource(new WebResource(manifest, options));
        }
    }
}
