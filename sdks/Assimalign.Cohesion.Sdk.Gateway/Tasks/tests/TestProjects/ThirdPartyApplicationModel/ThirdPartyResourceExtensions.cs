using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Provides the fake third-party typed resource factory used by the package-boundary test.</summary>
public static class ThirdPartyResourceExtensions
{
    /// <summary>Adds a fake third-party manifest with typed planning options.</summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="manifest">The build-produced resource manifest.</param>
    /// <param name="options">The typed planning options.</param>
    /// <returns>The generic descriptor returned by the application builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public static IApplicationResourceDescriptor AddThirdParty(
        IApplicationBuilder builder,
        ResourceManifest manifest,
        ThirdPartyResourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);
        return builder.AddResource(manifest, options);
    }
}
