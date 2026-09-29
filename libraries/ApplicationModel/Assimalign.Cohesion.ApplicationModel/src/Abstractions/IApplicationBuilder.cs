using System;
using System.ComponentModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Fluently assembles an <see cref="IApplicationModel"/> and selects the gateway that
/// will realize it, then produces a runnable <see cref="IApplication"/>.
/// </summary>
/// <remarks>
/// Orchestration packages' <c>Use&lt;Area&gt;(...)</c> verbs write <see cref="Providers"/>, and
/// <see cref="Build"/> copies those registrations into the built model as a frozen snapshot
/// (<see cref="IApplicationModel.Providers"/>) and validates it against the built graph. Later
/// registrations never reach an already-built model. This interface deliberately does not extend
/// <see cref="IApplicationProviderBuilder"/>: that surface belongs to application-set members, whose
/// verbs bind stores by <see cref="ResourceName"/>, while a builder's verbs bind the resource
/// descriptors it created. The default builder implements both interfaces.
/// </remarks>
public interface IApplicationBuilder
{
    /// <summary>
    /// Gets the application's mutable provider registrations: mount-source providers, the
    /// certificate authority, the trusted-issuer store, command-input resolvers, the telemetry
    /// sink, the credential issuer, and control-plane caller authenticators.
    /// </summary>
    /// <remarks>
    /// Orchestration packages' <c>Use&lt;Area&gt;(...)</c> verbs write this instance;
    /// <see cref="Build"/> snapshots and validates it.
    /// </remarks>
    ApplicationProviders Providers { get; }

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
    /// Registers a build-produced application-boundary declaration. Generated gateway code
    /// uses this infrastructure seam; application code binds it through <c>RemoteReference</c>.
    /// </summary>
    /// <param name="declaration">The generated external declaration.</param>
    /// <param name="resolver">The code binding, or <see langword="null"/> to leave it unbound.</param>
    /// <returns>The descriptor representing the external node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="declaration"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The declaration conflicts with another external of the same name, or its embedded
    /// manifest identity does not match the declaration.
    /// </exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    IApplicationResourceDescriptor AddExternal(
        ExternalResourceDeclaration declaration,
        IExternalResourceResolver? resolver = null);

    /// <summary>Registers a command whose graph membership and manifest support are validated at Build.</summary>
    /// <param name="command">The declaration owned by this application.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
    IApplicationBuilder AddCommand(IResourceCommand command);

    /// <summary>
    /// Selects the gateway that will realize the model. Required: <see cref="Build"/>
    /// throws when no gateway has been selected. Returns the builder for chaining.
    /// </summary>
    /// <param name="gateway">The gateway that will realize the model.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="gateway"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Local and InProcess gateways use the Local environment when no environment option or
    /// nonblank process environment variable is supplied. Reselecting a gateway recalculates
    /// that default; explicit environment values are preserved.
    /// </remarks>
    IApplicationBuilder UseGateway(IApplicationGateway gateway);

    /// <summary>
    /// Validates the graph, application identity, resource manifests, typed overrides,
    /// planner diagnostics, realization plans, resource commands, and provider registrations,
    /// then returns the runnable application.
    /// </summary>
    /// <returns>The built application.</returns>
    /// <exception cref="InvalidOperationException">
    /// No gateway was selected; no resources are realized; or the application name, resource
    /// graph, manifest, typed override, planner diagnostic, realization plan, or command is invalid.
    /// Commands must have unique identities and one declaration per target ownership key.
    /// Provider registrations are invalid when a <c>&lt;source&gt;:&lt;key&gt;</c> mount source of
    /// the application's own manifests has no <see cref="ApplicationProviders.Sources"/> entry, when
    /// a mount source or provider binding names a resource of another application (cross-application
    /// store sources are not supported yet), or when a bound resource is missing or has a kind other
    /// than the provider's <c>ResourceKind</c>.
    /// </exception>
    /// <remarks>
    /// After each resource plan validates, this method writes one informational
    /// planner-to-compiler path to standard error in resource declaration order.
    /// Standard output remains reserved for machine-readable describe and render output.
    /// </remarks>
    IApplication Build();
}
