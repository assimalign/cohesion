using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.VpnGateway.ApplicationModel;

/// <summary>Composition extensions for VpnGateway manifests.</summary>
public static class VpnGatewayResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>Adds a manifest-backed VpnGateway resource.</summary>
        /// <param name="manifest">The build-produced VpnGateway manifest.</param>
        /// <param name="options">Optional resource planning overrides.</param>
        /// <returns>The resource descriptor for composing dependency edges.</returns>
        /// <exception cref="ArgumentNullException">
        /// The builder or <paramref name="manifest"/> is null.
        /// </exception>
        public IVpnGatewayResourceDescriptor AddVpnGateway(
            ResourceManifest manifest,
            VpnGatewayResourceOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(manifest);
            return new VpnGatewayResourceDescriptor(builder.AddResource(new VpnGatewayResource(manifest, options)));
        }
    }
}
