using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.LogSpace.Hosting.ApplicationModel.Internal;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.LogSpace.Hosting.ApplicationModel;

/// <summary>
/// Registers a LogSpace application as a managed resource instance.
/// </summary>
public static class LogSpaceResourceExtensions
{
    extension(IServiceProviderBuilder services)
    {
        /// <summary>
        /// Registers the application as a LogSpace resource instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The resource is a registration like every other input the application composes, made on the
        /// LogSpace application's <c>builder.Services</c>: a lifecycle service the container creates
        /// once, when the application is built. The application keeps its own composition. Actions the
        /// application defines are added with
        /// <see cref="ApplicationInstanceOptions.AddAction(IApplicationInstanceAction, System.Text.Json.Serialization.JsonSerializerContext)"/>.
        /// </para>
        /// <para>
        /// The resource opts into each platform it runs on through that platform's hosting package,
        /// such as <c>resource =&gt; resource.UseInProcess()</c>. When the instance's environment names
        /// a platform (<see cref="ApplicationInstanceOptions.Platform"/>), the instance is managed
        /// through that platform's client: before any of the application's services start, it
        /// publishes its manifest and restores the state the gateway answers with, and from then until
        /// the application stops the gateway can call its control plane. A publication the gateway
        /// refuses or cannot receive fails the application's start. The resource does no transport of
        /// its own: how the manifest travels and how the gateway's calls arrive is the platform's.
        /// </para>
        /// <para>
        /// Without a named platform the application runs unmanaged, and nothing is registered.
        /// </para>
        /// </remarks>
        /// <param name="name">The resource's name, such as <c>logs</c>.</param>
        /// <param name="configure">Opts the resource into its platforms and adds the application's own actions.</param>
        /// <returns>The same registrations for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// The instance's environment names a platform the resource uses no client for.
        /// </exception>
        public IServiceProviderBuilder AddResource(ResourceName name, Action<LogSpaceResourceOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
            }

            var options = new LogSpaceResourceOptions(name);
            configure?.Invoke(options);

            if (options.GetGatewayClient() is not { } client)
            {
                return services;
            }

            // Control-plane content is JSON, written through the context each added action came with.
            IApplicationContentSerializer serializer = JsonApplicationContentSerializer.Create([.. options.JsonContexts]);

            // The added actions resolve the application's services, so the control plane is created
            // once the application's container exists. The resource service publishes and restores in
            // StartingAsync, which the host completes for every lifecycle service before it starts any,
            // and opens the control plane to the gateway's calls in StartAsync.
            DeploymentId? deployment = options.Deployment;
            services.AddSingleton<IHostService>(provider => ApplicationInstanceService.Create(
                ApplicationInstanceControlPlane.Create(
                    new LogSpaceResource(name, deployment, options.CreateActions(provider)),
                    serializer),
                client));

            return services;
        }
    }
}
