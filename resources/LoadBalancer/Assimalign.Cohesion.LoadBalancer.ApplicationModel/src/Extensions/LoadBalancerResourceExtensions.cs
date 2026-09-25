using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Composition extensions for LoadBalancer manifests.</summary>
public static class LoadBalancerResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed LoadBalancer resource.</summary>
        /// <param name="manifest">The build-produced LoadBalancer manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public ILoadBalancerResourceDescriptor AddLoadBalancer(
            ResourceManifest manifest,
            LoadBalancerResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new LoadBalancerResourceDescriptor(builder.AddResource(new LoadBalancerResource(manifest, options)));
        }
    }
}
