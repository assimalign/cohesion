using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Fluently assembles an <see cref="IApplicationModel"/> and selects the gateway that
/// will realize it, then produces a runnable <see cref="IApplication"/>.
/// </summary>
public interface IApplicationBuilder
{
    /// <summary>
    /// Gets the environment selected from <c>--environment</c> or the process environment.
    /// </summary>
    IApplicationEnvironment Environment { get; }

    /// <summary>
    /// Gets the operation selected by <c>--mode</c>.
    /// </summary>
    GatewayRunMode RunMode { get; }

    /// <summary>
    /// Gets the gateway identity requested by <c>--gateway</c>, or <see langword="null"/>
    /// when provider selection should use its normal default.
    /// </summary>
    ResourceName? RequestedGateway { get; }

    /// <summary>
    /// Sets the application identity used by the model and by platform ownership checks.
    /// </summary>
    /// <param name="name">The application name. It is validated as an RFC1123 label by <see cref="Build"/>.</param>
    /// <returns>This builder.</returns>
    IApplicationBuilder UseName(ApplicationName name);

    /// <summary>
    /// Adds a resource to the model and returns its descriptor so dependency edges can
    /// be chained fluently (for example <c>builder.AddWebApp("admin").DependsOn(identity)</c>).
    /// </summary>
    /// <param name="resource">The resource to add.</param>
    /// <returns>The descriptor wrapping <paramref name="resource"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    IApplicationResourceDescriptor AddResource(IApplicationResource resource);

    /// <summary>
    /// Adds a manifest-backed resource using the common platform-neutral planning options.
    /// Planning is deferred until <see cref="Build"/>.
    /// </summary>
    /// <param name="manifest">The build-produced resource manifest.</param>
    /// <returns>The descriptor wrapping the manifest-backed resource.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A resource with the same name was already added.</exception>
    IApplicationResourceDescriptor AddResource(ResourceManifest manifest);

    /// <summary>
    /// Adds a manifest-backed resource with typed, platform-neutral deployer options.
    /// Planning is deferred until <see cref="Build"/>.
    /// </summary>
    /// <typeparam name="TOptions">The resource area's planning-option type.</typeparam>
    /// <param name="manifest">The build-produced resource manifest.</param>
    /// <param name="options">The deployer-owned planning overrides.</param>
    /// <returns>The descriptor wrapping the manifest-backed resource.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifest"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">A resource with the same name was already added.</exception>
    IApplicationResourceDescriptor AddResource<TOptions>(ResourceManifest manifest, TOptions options)
        where TOptions : class, IResourceOptions;

    /// <summary>
    /// Adds a resource produced from the in-progress model, letting a resource read
    /// already-declared peers (for example to bind an endpoint to a dependency).
    /// </summary>
    /// <param name="configure">A factory that produces the resource from the current model.</param>
    /// <returns>The descriptor wrapping the produced resource.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    IApplicationResourceDescriptor AddResource(Func<IApplicationModel, IApplicationResource> configure);

    /// <summary>
    /// Selects the gateway that will realize the model. Required: <see cref="Build"/>
    /// throws when no gateway has been selected. Returns the builder for chaining.
    /// </summary>
    /// <param name="gateway">The gateway that will realize the model.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gateway"/> is <see langword="null"/>.</exception>
    IApplicationBuilder UseGateway(IApplicationGateway gateway);

    /// <summary>
    /// Validates the graph, application identity, resource manifests, typed overrides,
    /// planner diagnostics, and realization plans, then returns the runnable application.
    /// </summary>
    /// <returns>The built application.</returns>
    /// <exception cref="InvalidOperationException">
    /// No gateway was selected; no resources are realized; or the application name, resource
    /// graph, manifest, typed override, planner diagnostic, or realization plan is invalid.
    /// </exception>
    /// <remarks>
    /// After each resource plan validates, this method writes one informational
    /// planner-to-compiler path to standard error in resource declaration order.
    /// Standard output remains reserved for machine-readable describe and render output.
    /// </remarks>
    IApplication Build();
}
