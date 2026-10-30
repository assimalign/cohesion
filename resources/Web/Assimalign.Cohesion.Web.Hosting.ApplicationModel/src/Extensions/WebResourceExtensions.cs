using System;

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
        /// The resource opts into each platform it runs on through that platform's hosting package,
        /// such as <c>resource =&gt; resource.UseInProcess()</c>. When the instance's environment names
        /// a platform (<see cref="ApplicationInstanceOptions.Platform"/>), the instance is managed
        /// through that platform's client: before any of the application's services or servers start,
        /// it publishes its manifest and restores the state the gateway answers with, and from then
        /// until the application's servers have drained the gateway can call its control plane. A
        /// publication the gateway refuses or cannot receive fails the application's start. The
        /// resource does no transport of its own: how the manifest travels and how the gateway's calls
        /// arrive is the platform's.
        /// </para>
        /// <para>
        /// Without a named platform the application runs unmanaged, with the settings its code defines.
        /// </para>
        /// </remarks>
        /// <param name="name">The resource's name, such as <c>orders-api</c>.</param>
        /// <param name="configure">Opts the resource into its platforms and adds the application's own actions.</param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// The instance's environment names a platform the resource uses no client for.
        /// </exception>
        public WebApplicationBuilder AddResource(ResourceName name, Action<WebResourceOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            if (name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
            }

            var options = new WebResourceOptions(name);
            configure?.Invoke(options);

            if (options.GetGatewayClient() is not { } client)
            {
                return builder;
            }

            // Control-plane content is JSON. The resource's own actions write their types through its
            // internal context, and each action the application added through the context it came with.
            IApplicationContentSerializer serializer = JsonApplicationContentSerializer.Create([WebResourceJsonContext.Default, .. options.JsonContexts]);

            // The control plane exposes the settings the application registered, so it is created once
            // the application's container exists. The resource service publishes and restores in
            // StartingAsync, which the host completes for every lifecycle service before it starts any
            // service or server, and opens the control plane to the gateway's calls in StartAsync; the
            // Web host stops it only after the application's servers have drained.
            Func<WebApplicationContext, IApplicationInstanceAction>[] addedActions = [.. options.Actions];
            DeploymentId? deployment = options.Deployment;
            builder.AddService(context => ApplicationInstanceService.Create(
                ApplicationInstanceControlPlane.Create(new WebResource(name, deployment, context.Settings, CreateAddedActions(addedActions, context)), serializer),
                client));

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
