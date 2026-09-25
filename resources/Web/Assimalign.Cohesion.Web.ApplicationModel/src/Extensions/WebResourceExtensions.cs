using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

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
        public IWebResourceDescriptor AddWeb(
            ResourceManifest manifest,
            WebResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            return new WebResourceDescriptor(builder.AddResource(new WebResource(manifest, options)));
        }

        /// <summary>Binds a manifest-backed remote Web resource with its typed command surface.</summary>
        /// <param name="declaration">The build-produced external declaration.</param>
        /// <param name="configure">The peer gateway, file, endpoint, or contributed binding.</param>
        /// <returns>The typed external graph descriptor.</returns>
        /// <exception cref="ArgumentNullException">The builder, declaration, or configuration callback is null.</exception>
        /// <exception cref="ArgumentException">The declaration has no Web manifest.</exception>
        /// <exception cref="InvalidOperationException">The external conflicts with an existing declaration.</exception>
        public IWebResourceDescriptor RemoteReferenceWeb(
            ExternalResourceDeclaration declaration,
            Action<RemoteReferenceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(declaration);
            if (!string.Equals(declaration.Manifest?.Kind, "Web", StringComparison.Ordinal))
            {
                throw new ArgumentException("A typed Web reference requires an Web manifest.", nameof(declaration));
            }
            return new WebResourceDescriptor(builder.RemoteReference(declaration, configure));
        }
    }
}
