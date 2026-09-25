using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Composition extensions for LogSpace manifests.</summary>
public static class LogSpaceResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed LogSpace resource.</summary>
        /// <param name="manifest">The build-produced LogSpace manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public ILogSpaceResourceDescriptor AddLogSpace(
            ResourceManifest manifest,
            LogSpaceResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new LogSpaceResourceDescriptor(builder.AddResource(new LogSpaceResource(manifest, options)));
        }
    }
}
