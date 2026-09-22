using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Composition extensions for ApiManager manifests.</summary>
public static class ApiManagerResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed ApiManager resource.</summary>
        /// <param name="manifest">The build-produced ApiManager manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public IApiManagerResourceDescriptor AddApiManager(
            ResourceManifest manifest,
            ApiManagerResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new ApiManagerResourceDescriptor(builder.AddResource(new ApiManagerResource(manifest, options)));
        }
    }
}
