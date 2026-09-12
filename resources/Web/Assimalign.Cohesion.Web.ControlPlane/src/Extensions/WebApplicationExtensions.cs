using System;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.ControlPlane;

/// <summary>Installs the resource control-plane protocol on a private Web listener.</summary>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>Serves resource management and probe routes before subsequent middleware.</summary>
        /// <param name="controlPlane">The resource's registered control plane.</param>
        /// <param name="resourceContext">The ambient resource identity, endpoints, and application trust key.</param>
        /// <param name="isApplicationReady">Reports whether the owning resource host has completed startup.</param>
        /// <returns>The same pipeline builder.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="InvalidOperationException">A managed resource lacks a valid identity or public trust key.</exception>
        /// <remarks>Install first on a private listener. Bare probes are public; namespaced routes require an ES256 bearer token when a gateway identity is present.</remarks>
        public IWebApplicationPipelineBuilder UseResourceControlPlane(
            IResourceControlPlane controlPlane,
            ResourceContext resourceContext,
            Func<bool> isApplicationReady)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(controlPlane);
            ArgumentNullException.ThrowIfNull(resourceContext);
            ArgumentNullException.ThrowIfNull(isApplicationReady);
            if (resourceContext.GatewayName is not null)
            {
                if (string.IsNullOrWhiteSpace(resourceContext.ResourceName))
                {
                    throw new InvalidOperationException("A gateway-managed resource requires an ambient resource name.");
                }
                using var verifier = new BootstrapTokenVerifier(resourceContext);
            }

            return builder.Use(next => context => ResourceControlPlaneMiddleware.InvokeAsync(
                controlPlane, resourceContext, isApplicationReady(), context, next));
        }
    }
}
