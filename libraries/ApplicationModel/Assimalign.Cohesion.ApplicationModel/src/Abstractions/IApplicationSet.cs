using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Composes independently described application models into one gateway instance.
/// </summary>
/// <remarks>
/// <para>
/// A member's model arrives from its gateway's describe output or an export, which carries no
/// provider registrations (<see cref="ApplicationProviders.Empty"/>): providers are code. A member
/// whose resources read stores, or that needs a certificate authority, a trust store, or a
/// telemetry sink, registers them explicitly for that member with
/// <see cref="AddApplication(ApplicationDeclaration, Action{IApplicationProviderBuilder})"/>. A member
/// never inherits another member's registrations, or anything registered by name elsewhere.
/// </para>
/// <code>
/// IApplicationSet set = Application.CreateSet(new LocalGateway(options), args)
///     .AddApplication(Applications.Platform, platform =&gt; platform
///         .UseSecretStore("platform-secrets")
///         .AsCertificateAuthority()
///         .AsTrustStore())
///     .AddApplication(Applications.AppA, appa =&gt; appa.UseConfigurationStore("appa-configuration"))
///     .AddApplication(Applications.AppB);
/// </code>
/// </remarks>
public interface IApplicationSet
{
    /// <summary>Gets the environment in which member models are resolved.</summary>
    IApplicationEnvironment Environment { get; }

    /// <summary>Gets the operation selected for this application-set invocation.</summary>
    GatewayRunMode RunMode { get; }

    /// <summary>
    /// Adds an application in deterministic reconciliation order, with no provider registrations of
    /// the set's own.
    /// </summary>
    /// <param name="application">The generated member application declaration.</param>
    /// <returns>This application set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="application"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An application with the same name is already present.
    /// </exception>
    /// <remarks>
    /// The member keeps the registrations its resolved model carries: none for a model imported from
    /// a describe output or an export. <see cref="RunAsync"/> still validates the member's own
    /// <c>&lt;source&gt;:&lt;key&gt;</c> mount sources, so a member whose resources read a store fails
    /// at set start with the registration to add.
    /// </remarks>
    IApplicationSet AddApplication(ApplicationDeclaration application);

    /// <summary>
    /// Adds an application in deterministic reconciliation order and registers the providers the
    /// gateway resolves that member's stores, certificates, trust, command inputs, telemetry,
    /// credentials, and control-plane callers through.
    /// </summary>
    /// <param name="application">The generated member application declaration.</param>
    /// <param name="configure">
    /// Registers the member's providers on its <see cref="IApplicationProviderBuilder"/>, typically
    /// through orchestration package verbs that take a resource name, for example
    /// <c>member =&gt; member.UseSecretStore("secrets").AsCertificateAuthority()</c>.
    /// </param>
    /// <returns>This application set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="application"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An application with the same name is already present.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <see cref="RunAsync"/> invokes <paramref name="configure"/> once per run, after it resolves
    /// the member's model, so <see cref="IApplicationProviderBuilder.TryGetResourceManifest"/> sees
    /// the member's resources and a verb can check a store's kind. When the callback returns, the set
    /// freezes the registrations, attaches them to that member's model alone as
    /// <see cref="IApplicationModel.Providers"/>, and validates them with the rules
    /// <see cref="IApplicationBuilder.Build"/> applies: every <c>&lt;source&gt;:&lt;key&gt;</c> mount of
    /// the member's own resources has a provider, a bound resource is a resource of the member with
    /// the provider's kind, and no source or binding reaches another application (cross-application
    /// store sources are not supported yet; this includes a Local <c>--realize</c> closure's
    /// resources). Registering on the surface after the callback returns throws.
    /// </para>
    /// <para>
    /// Registrations are per member and never inherited: a member gets exactly what its own callback
    /// registered, nothing another member registered under the same source or resource name.
    /// </para>
    /// </remarks>
    IApplicationSet AddApplication(
        ApplicationDeclaration application,
        Action<IApplicationProviderBuilder> configure);

    /// <summary>
    /// Resolves all member models at start, composes Describe output, or dispatches the
    /// collection through one gateway for lifecycle, Render, and Bootstrap operations.
    /// </summary>
    /// <param name="cancellationToken">Signals shutdown or cancellation.</param>
    /// <returns>A task representing the application-set lifetime.</returns>
    /// <exception cref="InvalidOperationException">
    /// No applications were declared, a resolved model has the wrong identity, a member's provider
    /// registration callback fails, a member's provider registrations are invalid for its model (the
    /// message names the member application), or the requested external realization cannot be
    /// honored.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The selected gateway does not implement the requested optional operation, or the run mode
    /// cannot be composed by an application set.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    /// <remarks>
    /// Members are resolved, bound to their registrations, and validated in declaration order in
    /// every run mode, before the gateway is contacted, the same way <see cref="IApplicationBuilder.Build"/>
    /// validates a builder's registrations whatever mode it runs in.
    /// </remarks>
    Task RunAsync(CancellationToken cancellationToken = default);
}
