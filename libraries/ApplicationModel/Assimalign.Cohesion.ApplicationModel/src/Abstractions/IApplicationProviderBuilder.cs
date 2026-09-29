using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The provider-registration surface of one application: the <see cref="ApplicationProviders"/> a
/// gateway resolves that application's stores, certificates, trust, command inputs, telemetry,
/// credentials, and control-plane callers through, and a lookup of the application's resources by
/// name so a registration verb can check what it binds.
/// </summary>
/// <remarks>
/// <para>
/// An application set hands one to the callback of
/// <see cref="IApplicationSet.AddApplication(ApplicationDeclaration, System.Action{IApplicationProviderBuilder})"/>
/// for a member application whose model it imports from a document, because providers are code and
/// a member's describe output carries none. It is a separate interface from
/// <see cref="IApplicationBuilder"/>, which does not extend it. The default builder implements both,
/// so the instance <c>Application.CreateBuilder(...)</c> returns can be cast to this surface; an
/// <see cref="IApplicationBuilder"/> reference does not convert to it implicitly, another builder
/// implementation need not implement it, and an application built in code binds stores through the
/// descriptor verbs instead.
/// </para>
/// <para>
/// Orchestration packages extend this interface with <c>Use&lt;Area&gt;(...)</c> verbs that bind a
/// store by <see cref="ResourceName"/>, beside the <see cref="IApplicationBuilder"/> verbs that bind
/// a resource descriptor; both forms make the same registrations, each in its receiver's
/// <see cref="ApplicationProviders"/> (this surface's <see cref="Providers"/>, a builder's
/// <see cref="IApplicationBuilder.Providers"/>), which on the default builder is one instance. A surface
/// registers providers for its own application only: a member never inherits another member's
/// registrations, or the application set's, by source or resource name.
/// </para>
/// </remarks>
public interface IApplicationProviderBuilder
{
    /// <summary>
    /// Gets the application whose providers this surface registers, or <see langword="null"/> while
    /// the default builder, which also implements this surface, has no explicit name (an unnamed Local
    /// builder derives one at <see cref="IApplicationBuilder.Build"/>).
    /// </summary>
    ApplicationName? Application { get; }

    /// <summary>
    /// Gets the application's mutable provider registrations: mount-source providers, the
    /// certificate authority, the trusted-issuer store, command-input resolvers, the telemetry
    /// sink, the credential issuer, and control-plane caller authenticators.
    /// </summary>
    /// <remarks>
    /// Nothing is registered by convention; orchestration packages add their providers through
    /// explicit verbs. The instance stays mutable while the application is composed. The model the
    /// gateway receives carries a frozen snapshot in <see cref="IApplicationModel.Providers"/>,
    /// validated against the application's own resources: <see cref="IApplicationBuilder.Build"/>
    /// takes the snapshot for a builder, and an application set takes it when its registration
    /// callback returns.
    /// </remarks>
    ApplicationProviders Providers { get; }

    /// <summary>
    /// Finds the manifest of a resource of the application's model by name, so a registration verb
    /// can check a resource's kind before binding a provider to it.
    /// </summary>
    /// <param name="resource">The resource name.</param>
    /// <param name="manifest">
    /// When this method returns <see langword="true"/>, the resource's manifest; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the model has a resource of that name whose manifest is known;
    /// <see langword="false"/> when it has none, or when a builder's resource carries no manifest
    /// until <see cref="IApplicationBuilder.Build"/>.
    /// </returns>
    /// <remarks>
    /// The lookup does not filter by owning application: a Local <c>--realize</c> closure's resources
    /// belong to another application and are found too, carrying that application in
    /// <see cref="ResourceManifest.Application"/>. Provider validation, not this lookup, rejects a
    /// binding to another application's resource. A <see langword="false"/> result is not an error by
    /// itself: validation reports a binding to a resource the application does not declare.
    /// </remarks>
    bool TryGetResourceManifest(ResourceName resource, [NotNullWhen(true)] out ResourceManifest? manifest);
}
