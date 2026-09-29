using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

// Deviates from the repo interface-first rule per the owner-approved BYO design (2026-09-25): a
// sealed registration container that this package freezes into the built model; the behaviours it
// holds are the provider interfaces, and nothing substitutes the container itself.

/// <summary>
/// The provider registrations an application hands to its gateway: mount-source providers, the
/// certificate authority, the trusted-issuer store, command-input resolvers, the telemetry sink,
/// the credential issuer, and additional control-plane caller authenticators.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is registered by convention. A gateway resolves a <c>&lt;source&gt;:&lt;key&gt;</c>
/// mount source, issues a TLS leaf through a certificate-authority resource, persists a trusted
/// issuer, rewrites a command payload, or exports telemetry only through a provider that the
/// application registered here. The seams are defined in this library; the shipped
/// implementations live in opt-in <c>Assimalign.Cohesion.&lt;Area&gt;.ApplicationModel.Orchestration</c>
/// packages whose <c>Use&lt;Area&gt;(store)</c> verbs fill these members.
/// </para>
/// <para>
/// The instance exposed by <see cref="IApplicationBuilder.Providers"/> and by
/// <see cref="IApplicationProviderBuilder.Providers"/> is mutable. For an
/// application built in code, <see cref="IApplicationBuilder.Build"/> copies it into the built
/// model as a frozen snapshot, so later builder registrations never reach an already-built model,
/// and validates the snapshot against the built graph before the gateway sees the model. For an
/// application-set member imported from a document, the set takes the snapshot when the member's
/// registration callback returns and attaches it to that member's model alone
/// (<see cref="IApplicationSet.AddApplication(ApplicationDeclaration, Action{IApplicationProviderBuilder})"/>).
/// On a frozen instance — <see cref="Empty"/>, or <see cref="IApplicationModel.Providers"/> of a
/// built or set-bound model — <see cref="Sources"/>, <see cref="CommandInputs"/>, and
/// <see cref="Callers"/> are read-only collections that throw <see cref="NotSupportedException"/>
/// on mutation, and every property setter throws <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
public sealed class ApplicationProviders
{
    private readonly bool _isFrozen;
    private ResourceProviderBinding<IResourceCertificateAuthority>? _certificateAuthority;
    private ResourceProviderBinding<ITrustedIssuerStore>? _trustStore;
    private ResourceTelemetrySink? _telemetry;
    private IApplicationCredentialIssuer? _credentialIssuer;

    /// <summary>
    /// Initializes an empty, mutable set of provider registrations.
    /// </summary>
    public ApplicationProviders()
    {
        Sources = new ResourceSourceProviderDictionary();
        CommandInputs = new ProviderList<IResourceCommandInputResolver>();
        Callers = new ProviderList<IApplicationCallerAuthenticator>();
    }

    private ApplicationProviders(ApplicationProviders source)
    {
        Sources = new ReadOnlyDictionary<string, IResourceSourceProvider>(
            new Dictionary<string, IResourceSourceProvider>(source.Sources, StringComparer.Ordinal));
        CommandInputs = Array.AsReadOnly(Copy(source.CommandInputs));
        Callers = Array.AsReadOnly(Copy(source.Callers));
        _certificateAuthority = source._certificateAuthority;
        _trustStore = source._trustStore;
        _telemetry = source._telemetry;
        _credentialIssuer = source._credentialIssuer;
        _isFrozen = true;
    }

    /// <summary>
    /// Gets a frozen instance with no registrations. It is the value of
    /// <see cref="IApplicationModel.Providers"/> for models that carry no registrations.
    /// </summary>
    public static ApplicationProviders Empty { get; } = new ApplicationProviders().ToFrozen();

    /// <summary>
    /// Gets the mount-source providers, keyed by the <c>&lt;source&gt;</c> part of a
    /// <c>&lt;source&gt;:&lt;key&gt;</c> mount source: a model resource name (a store resource)
    /// or a source outside the model (for example <c>vault</c>).
    /// </summary>
    /// <remarks>
    /// Keys compare ordinally. <c>parameter:</c> and <c>literal:</c> sources are resolved by the
    /// gateway itself, so <c>parameter</c> and <c>literal</c> are rejected as keys, as are blank
    /// keys, keys containing <c>:</c>, and <see langword="null"/> providers
    /// (<see cref="ArgumentException"/> / <see cref="ArgumentNullException"/>). The collection is
    /// read-only once frozen.
    /// </remarks>
    public IDictionary<string, IResourceSourceProvider> Sources { get; }

    /// <summary>
    /// Gets or sets the certificate authority that issues TLS leaves for endpoints declaring a
    /// certificate mount without a source, or <see langword="null"/> to leave the gateway on its
    /// Local-only development authority.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is set on a frozen instance.</exception>
    public ResourceProviderBinding<IResourceCertificateAuthority>? CertificateAuthority
    {
        get => _certificateAuthority;
        set
        {
            ThrowIfFrozen();
            _certificateAuthority = value;
        }
    }

    /// <summary>
    /// Gets or sets the store that persists trusted peer issuers, or <see langword="null"/> to
    /// leave the gateway on its Local-only file store.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is set on a frozen instance.</exception>
    public ResourceProviderBinding<ITrustedIssuerStore>? TrustStore
    {
        get => _trustStore;
        set
        {
            ThrowIfFrozen();
            _trustStore = value;
        }
    }

    /// <summary>
    /// Gets the resolvers that rewrite declared command payloads before delivery, each keyed by
    /// its <see cref="IResourceCommandInputResolver.CommandKind"/>. A command kind with no
    /// resolver is delivered exactly as declared, unless the target resource's manifest marks the
    /// kind <see cref="ResourceManifestCommand.RequiresInputResolver"/>: then
    /// <see cref="IApplicationBuilder.Build"/>, or an application set starting the member, rejects
    /// the application and names the registration to add.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> entries are rejected with <see cref="ArgumentNullException"/>. The
    /// collection is read-only once frozen.
    /// </remarks>
    public IList<IResourceCommandInputResolver> CommandInputs { get; }

    /// <summary>
    /// Gets or sets the telemetry sink resources export to, or <see langword="null"/> to inject no
    /// telemetry settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is set on a frozen instance.</exception>
    public ResourceTelemetrySink? Telemetry
    {
        get => _telemetry;
        set
        {
            ThrowIfFrozen();
            _telemetry = value;
        }
    }

    /// <summary>
    /// Gets or sets the issuer consulted first for every credential the gateway mints, or
    /// <see langword="null"/> to use the gateway's default ES256 application-key issuer.
    /// </summary>
    /// <remarks>
    /// The gateway routes every credential it mints for the application through this issuer: each
    /// resource's bootstrap credential, its own calls to resource control planes and stores,
    /// telemetry-emitter credentials, calls to peer gateways, and exported developer tokens (see
    /// <see cref="ApplicationCredentialPurpose"/>). An issuer that returns <see langword="null"/> for a
    /// request defers that request to the default issuer. An application that registers an issuer
    /// must register a matching credential verifier on its resources
    /// (<c>ResourceRuntime.RegisterCredentialVerifier</c> in <c>Assimalign.Cohesion.Hosting.Resources</c>).
    /// </remarks>
    /// <exception cref="InvalidOperationException">The value is set on a frozen instance.</exception>
    public IApplicationCredentialIssuer? CredentialIssuer
    {
        get => _credentialIssuer;
        set
        {
            ThrowIfFrozen();
            _credentialIssuer = value;
        }
    }

    /// <summary>
    /// Gets the additional authenticators for gateway control-plane callers (developers and
    /// peers), consulted in order after the built-in trusted-issuer authenticator.
    /// </summary>
    /// <remarks>
    /// The gateway control plane consults them, in order, for a credential the built-in
    /// trusted-issuer authenticator does not verify; the first final status wins, and command
    /// ownership and command-kind checks apply to the mapped <see cref="ApplicationCaller"/>.
    /// <see langword="null"/> entries are rejected with <see cref="ArgumentNullException"/>. The
    /// collection is read-only once frozen.
    /// </remarks>
    public IList<IApplicationCallerAuthenticator> Callers { get; }

    /// <summary>
    /// Gets whether this instance is a frozen snapshot.
    /// </summary>
    internal bool IsFrozen => _isFrozen;

    /// <summary>
    /// Returns a frozen snapshot of the current registrations. A frozen instance returns itself;
    /// a mutable instance is copied and stays mutable.
    /// </summary>
    /// <returns>The frozen snapshot.</returns>
    internal ApplicationProviders ToFrozen() => _isFrozen ? this : new ApplicationProviders(this);

    /// <summary>
    /// Gets whether any role holds a registration.
    /// </summary>
    internal bool HasRegistrations =>
        Sources.Count != 0 ||
        CommandInputs.Count != 0 ||
        Callers.Count != 0 ||
        _certificateAuthority is not null ||
        _trustStore is not null ||
        _telemetry is not null ||
        _credentialIssuer is not null;

    private void ThrowIfFrozen()
    {
        if (_isFrozen)
        {
            throw new InvalidOperationException(
                "These provider registrations are frozen. Register providers through " +
                "IApplicationBuilder.Providers before Build(), or, for an application-set member, inside " +
                "its AddApplication(..., configure) callback; ApplicationProviders.Empty and a model's " +
                "Providers are read-only.");
        }
    }

    private static T[] Copy<T>(IList<T> source)
    {
        var copy = new T[source.Count];
        source.CopyTo(copy, 0);
        return copy;
    }
}
