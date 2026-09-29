using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Provides the fake third-party resource verb used by the package-boundary test.</summary>
public static class ThirdPartyResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a fake third-party manifest with typed planning options.</summary>
        /// <param name="manifest">The build-produced resource manifest.</param>
        /// <param name="options">Optional typed planning options.</param>
        /// <returns>The generic descriptor returned by the application builder.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is <see langword="null"/>.
        /// </exception>
        public IApplicationResourceDescriptor AddThirdParty(
            ResourceManifest manifest,
            ThirdPartyResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return builder.AddResource(manifest, options ?? new ThirdPartyResourceOptions());
        }
    }
}
