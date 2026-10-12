using System;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Resources.Internal;

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
        /// <exception cref="InvalidOperationException">A managed resource lacks a valid identity or public trust key, or the pipeline context has no <see cref="IWebApplicationLifetime"/>.</exception>
        /// <remarks>Install first on a private listener. The application lifetime owns the verification handle. Bare probes are public; namespaced routes require an ES256 bearer token when a gateway identity is present.</remarks>
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

            return builder.Use((application, next) =>
            {
                if (application is not IWebApplicationLifetime owner)
                {
                    if (resourceContext.GatewayName is not null)
                    {
                        throw new InvalidOperationException(
                            "Managed control-plane middleware requires an IWebApplicationLifetime. " +
                            "Manual hosts can own ResourceControlPlaneMiddleware.CreateLifetime and compose InvokeAsync.");
                    }
                    return context => ResourceControlPlaneMiddleware.InvokeAsync(
                        controlPlane, resourceContext, isApplicationReady(), controlPlanePort, context, next);
                }

                var lifetime = (ResourceControlPlaneLifetime)ResourceControlPlaneMiddleware.CreateLifetime(resourceContext);
                bool registered = false;
                try
                {
                    owner.RegisterForDisposal(lifetime);
                    registered = true;
                }
                finally
                {
                    if (!registered)
                    {
                        lifetime.Dispose();
                    }
                }

                return context =>
                {
                    lifetime.ThrowIfDisposed();
                    return ResourceControlPlaneMiddleware.InvokeAsync(
                        controlPlane, resourceContext, isApplicationReady(), controlPlanePort, context, next);
                };
            });
        }
    }
}
