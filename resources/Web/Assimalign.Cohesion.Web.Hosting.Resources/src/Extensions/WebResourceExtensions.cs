using System;
using System.Globalization;
using System.Net;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Resources;

/// <summary>
/// Runs a Web application as a managed resource instance.
/// </summary>
public static class WebResourceExtensions
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Makes the application a Web resource instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When the resource has a manifest endpoint (by default the
        /// <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable its gateway sets), the
        /// instance is managed. Before any of the application's services or servers start, it
        /// publishes its manifest and restores the state the gateway answers with; it then serves its
        /// control plane on <see cref="WebResourceBuilder.ControlPlaneEndpoint"/>, ahead of the
        /// application's servers and until they have drained. A publication the gateway refuses or
        /// cannot receive fails the application's start.
        /// </para>
        /// <para>
        /// Without a manifest endpoint the application runs unmanaged, with the state its code defines.
        /// </para>
        /// </remarks>
        /// <param name="name">The resource's name, such as <c>orders-api</c>.</param>
        /// <param name="configure">Configures the resource's endpoints and actions.</param>
        /// <returns>The resource instance.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="name"/> is <see langword="default"/>; the manifest endpoint is not an
        /// absolute http or https URL; the control-plane endpoint is not an absolute http URL whose
        /// host is an IP address or localhost; or the serializer does not know an action's state type.
        /// </exception>
        /// <exception cref="InvalidOperationException">The resource has a manifest endpoint but no control-plane endpoint.</exception>
        public WebResource AddResource(ResourceName name, Action<WebResourceBuilder>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            if (name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
            }

            var resourceBuilder = new WebResourceBuilder(name);
            configure?.Invoke(resourceBuilder);
            WebResource resource = resourceBuilder.Build();

            if (resourceBuilder.ManifestEndpoint is not { } manifestEndpoint)
            {
                return resource;
            }

            Uri controlPlaneEndpoint = resourceBuilder.ControlPlaneEndpoint
                ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.ControlPlaneEndpointRequired, AppEnvironment.Variables.ControlPlaneEndpoint));
            IPEndPoint listener = WebResourceControlPlaneServer.ParseEndpoint(controlPlaneEndpoint);

            IResourceContentSerializer serializer = resourceBuilder.Serializer;
            ResourceManifestPublisher publisher = ResourceManifestPublisher.Create(new ResourceManifestPublisherOptions
            {
                Endpoint = manifestEndpoint,
                HttpClientFactory = resourceBuilder.HttpClientFactory,
                Serializer = serializer,
            });
            ResourceControlPlane controlPlane = ResourceControlPlane.Create(resource, serializer);

            // The resource service publishes and restores in StartingAsync, which the host completes
            // for every lifecycle service before it starts any service or server. The control-plane
            // server is a host service of its own, so it listens only once the gateway's state is in
            // effect, and the Web host stops it only after the application's servers have drained.
            builder.AddService(_ => ResourceService.Create(controlPlane, publisher));
            builder.AddService(_ => new WebResourceControlPlaneServer(listener, controlPlane));

            return resource;
        }
    }

    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the CORS middleware whose default policy is the resource's <c>cors</c> action: a
        /// write or a restored snapshot changes the policy the next request is served under.
        /// </summary>
        /// <remarks>Register it where <c>UseCors</c> would go: after <c>UseRouting</c>, ahead of
        /// middleware that can reject a preflight.</remarks>
        /// <param name="resource">The resource whose CORS policy to serve.</param>
        /// <returns>The same pipeline builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="resource"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The resource has no CORS policy (<see cref="WebResourceBuilder.AddCors"/>).</exception>
        public IWebApplicationPipelineBuilder UseCors(WebResource resource)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(resource);

            WebCorsAction cors = resource.Cors ?? throw new InvalidOperationException(ErrorMessages.CorsNotAdded);
            return builder.Use(cors.Middleware);
        }
    }
}
