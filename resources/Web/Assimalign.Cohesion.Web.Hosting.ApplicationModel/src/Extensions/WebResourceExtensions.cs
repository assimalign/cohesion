using System;
using System.Globalization;
using System.Net;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Web.Hosting.ApplicationModel.Internal;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel;

/// <summary>
/// Runs a Web application as a managed resource instance.
/// </summary>
public static class WebResourceExtensions
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Makes the application a Web resource instance, exposing the settings its features
        /// registered as the resource's actions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The application keeps its own composition: a feature registers its setting the way it
        /// always does, and the resource exposes each setting it knows. The <c>cors</c> setting that
        /// <c>builder.Services.AddCors(...)</c> registers becomes the <c>cors</c> action, whose writes
        /// change the default policy the middleware <c>UseCors()</c> placed applies. Actions the
        /// application defines are added with <see cref="WebResourceOptions.AddAction(IApplicationInstanceAction, System.Text.Json.Serialization.JsonSerializerContext)"/>.
        /// </para>
        /// <para>
        /// When the resource has a manifest endpoint (by default the
        /// <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable its gateway sets, read
        /// through <see cref="AppEnvironment"/> so a gateway that runs the application in its own
        /// process can set it for this application alone), the instance is managed. Before any of the application's services or servers start, it
        /// publishes its manifest and restores the state the gateway answers with; it then serves its
        /// control plane on <see cref="WebResourceOptions.ControlPlaneEndpoint"/>, ahead of the
        /// application's servers and until they have drained. A publication the gateway refuses or
        /// cannot receive fails the application's start.
        /// </para>
        /// <para>
        /// Without a manifest endpoint the application runs unmanaged, with the settings its code defines.
        /// </para>
        /// </remarks>
        /// <param name="name">The resource's name, such as <c>orders-api</c>.</param>
        /// <param name="configure">Configures the resource's endpoints and adds the application's own actions.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="name"/> is <see langword="default"/>; the manifest endpoint is not an
        /// absolute http, https, or inprocess URL; or the control-plane endpoint is not an absolute
        /// http URL whose host is an IP address or localhost.
        /// </exception>
        /// <exception cref="InvalidOperationException">The resource has a manifest endpoint but no control-plane endpoint.</exception>
        public WebApplicationBuilder AddResource(ResourceName name, Action<WebResourceOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            if (name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
            }

            var options = new WebResourceOptions(name);
            configure?.Invoke(options);

            if (options.ManifestEndpoint is not { } manifestEndpoint)
            {
                return builder;
            }

            Uri controlPlaneEndpoint = options.ControlPlaneEndpoint
                ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.ControlPlaneEndpointRequired, AppEnvironment.Variables.ControlPlaneEndpoint));
            IPEndPoint listener = WebResourceControlPlaneServer.ParseEndpoint(controlPlaneEndpoint);

            // Control-plane content is JSON. The resource's own actions write their types through its
            // internal context, and each action the application added through the context it came with.
            IApplicationContentSerializer serializer = JsonApplicationContentSerializer.Create([WebResourceJsonContext.Default, .. options.JsonContexts]);
            ApplicationInstancePublisher publisher = ApplicationInstancePublisher.Create(new ApplicationInstancePublisherOptions
            {
                Endpoint = manifestEndpoint,
                HttpClientFactory = options.HttpClientFactory,
                Serializer = serializer,
            });

            // The control plane exposes the settings the application registered, so it is created once
            // the application's container exists: the first of the two services the build resolves
            // creates it, and the other shares it.
            Func<WebApplicationContext, IApplicationInstanceAction>[] addedActions = [.. options.Actions];
            DeploymentId? deployment = options.Deployment;
            ApplicationInstanceControlPlane? controlPlane = null;
            ApplicationInstanceControlPlane GetControlPlane(WebApplicationContext context)
            {
                return controlPlane ??= ApplicationInstanceControlPlane.Create(
                    new WebResource(name, deployment, context.Settings, CreateAddedActions(addedActions, context)),
                    serializer);
            }

            // The resource service publishes and restores in StartingAsync, which the host completes
            // for every lifecycle service before it starts any service or server. The control-plane
            // server is a host service of its own, so it listens only once the gateway's state is in
            // effect, and the Web host stops it only after the application's servers have drained.
            builder.AddService(context => ApplicationInstanceService.Create(GetControlPlane(context), publisher));
            builder.AddService(context => new WebResourceControlPlaneServer(listener, GetControlPlane(context)));

            return builder;
        }
    }

    private static IApplicationInstanceAction[] CreateAddedActions(Func<WebApplicationContext, IApplicationInstanceAction>[] factories, WebApplicationContext context)
    {
        var actions = new IApplicationInstanceAction[factories.Length];
        for (int i = 0; i < factories.Length; i++)
        {
            actions[i] = factories[i].Invoke(context)
                ?? throw new InvalidOperationException(ErrorMessages.ResourceActionFactoryResultNull);
        }

        return actions;
    }
}
