using System;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Resources;

/// <summary>Installs the resource control-plane protocol on a private Web listener.</summary>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>Serves resource management and probe routes before subsequent middleware.</summary>
        /// <param name="controlPlane">The resource's registered control plane.</param>
        /// <param name="resourceContext">The ambient resource identity, endpoints, and application trust key.</param>
        /// <param name="isApplicationReady">Reports whether the owning resource host has completed startup.</param>
        /// <param name="controlPlanePort">An optional listener-port gate for every route, including bare probes; null serves every listener.</param>
        /// <returns>The same pipeline builder.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="InvalidOperationException">A managed resource lacks a valid identity or public trust key.</exception>
        /// <remarks>Install first on a private listener. Bare probes are public; namespaced routes require an ES256 bearer token when a gateway identity is present.</remarks>
        public IWebApplicationPipelineBuilder UseResourceControlPlane(
            IResourceControlPlane controlPlane,
            ResourceContext resourceContext,
            Func<bool> isApplicationReady,
            int? controlPlanePort = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(controlPlane);
            ArgumentNullException.ThrowIfNull(resourceContext);
            ArgumentNullException.ThrowIfNull(isApplicationReady);
            ResourceControlPlaneMiddleware.Validate(resourceContext);

            return builder.Use(next => context => ResourceControlPlaneMiddleware.InvokeAsync(
                controlPlane, resourceContext, isApplicationReady(), controlPlanePort, context, next));
        }
    }
}
