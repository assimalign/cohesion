using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Composition extensions for adding database manifests to an application model.
/// </summary>
public static class DatabaseResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Adds a manifest-backed Cohesion database resource to the application.
        /// </summary>
        /// <param name="manifest">The build-produced database resource manifest.</param>
        /// <param name="options">
        /// Optional deployer-owned replica and storage overrides.
        /// </param>
        /// <returns>The resource descriptor, for chaining dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="manifest"/> is <see langword="null"/>.
        /// </exception>
        public IDatabaseResourceDescriptor AddDatabase(
            ResourceManifest manifest,
            DatabaseResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            return new DatabaseResourceDescriptor(builder.AddResource(new DatabaseResource(manifest, options)));
        }

        /// <summary>Binds a manifest-backed remote Database resource with its typed command surface.</summary>
        /// <param name="declaration">The build-produced external declaration.</param>
        /// <param name="configure">The peer gateway, file, endpoint, or contributed binding.</param>
        /// <returns>The typed external graph descriptor.</returns>
        /// <exception cref="ArgumentNullException">The builder, declaration, or configuration callback is null.</exception>
        /// <exception cref="ArgumentException">The declaration has no Database manifest.</exception>
        /// <exception cref="InvalidOperationException">The external conflicts with an existing declaration.</exception>
        public IDatabaseResourceDescriptor RemoteReferenceDatabase(
            ExternalResourceDeclaration declaration,
            Action<RemoteReferenceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(declaration);
            if (!string.Equals(declaration.Manifest?.Kind, "Database", StringComparison.Ordinal))
            {
                throw new ArgumentException("A typed Database reference requires an Database manifest.", nameof(declaration));
            }
            return new DatabaseResourceDescriptor(builder.RemoteReference(declaration, configure));
        }
    }
}
