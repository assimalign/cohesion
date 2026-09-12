using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.NatGateway.ApplicationModel;

/// <summary>Composition extensions for NatGateway manifests.</summary>
public static class NatGatewayResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed NatGateway resource.</summary>
        /// <param name="manifest">The build-produced NatGateway manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public INatGatewayResourceDescriptor AddNatGateway(
            ResourceManifest manifest,
            NatGatewayResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new NatGatewayResourceDescriptor(builder.AddResource(new NatGatewayResource(manifest, options)));
        }
    }
}
