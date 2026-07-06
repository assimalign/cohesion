using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents a registered OpenID Connect client (relying party) record: the metadata an
/// authorization server holds for a client after registration. Pre-registration request
/// shapes are <see cref="OpenIdConnectClientRegistrationRequest" /> — they have no client
/// identifier yet and therefore do not derive from <see cref="ProtocolMetadata" />.
/// </summary>
/// <remarks>
/// The client's logout and JWK Set URIs are projected into the inherited base endpoint
/// list (front-channel logout with a redirect binding, back-channel logout and JWK Set
/// with a back-channel binding) so protocol-neutral logout orchestration can enumerate
/// them; redirect URIs are deliberately not projected — they are per-request response
/// destinations, not published service endpoints. Secrets are never modeled; only the
/// secret's lifecycle metadata (<see cref="ClientSecretExpiresAt" />) is.
/// </remarks>
public sealed class OpenIdConnectClientMetadata : ProtocolMetadata
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectClientMetadata" /> class
    /// by snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The registered client contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a list entry is null or whitespace, or when a property name is blank
    /// or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no client identifier or a metadata list contains
    /// null entries.
    /// </exception>
    public OpenIdConnectClientMetadata(OpenIdConnectClientMetadataDescriptor descriptor)
        : base(PrepareBase(descriptor), AuthenticationProtocol.OpenIdConnect)
    {
        RedirectUris = ModelSnapshot.Strings(descriptor.RedirectUris, nameof(descriptor));
        PostLogoutRedirectUris = ModelSnapshot.Strings(descriptor.PostLogoutRedirectUris, nameof(descriptor));
        ResponseTypes = ModelSnapshot.Strings(descriptor.ResponseTypes, nameof(descriptor));
        GrantTypes = ModelSnapshot.Strings(descriptor.GrantTypes, nameof(descriptor));
        Scopes = ModelSnapshot.Strings(descriptor.Scopes, nameof(descriptor));
        Contacts = ModelSnapshot.Strings(descriptor.Contacts, nameof(descriptor));
        ApplicationType = descriptor.ApplicationType;
        ClientName = descriptor.ClientName;
        TokenEndpointAuthMethod = descriptor.TokenEndpointAuthMethod;
        SubjectType = descriptor.SubjectType;
        SectorIdentifierUri = descriptor.SectorIdentifierUri;
        IdTokenSignedResponseAlg = descriptor.IdTokenSignedResponseAlg;
        JwksUri = descriptor.JwksUri;
        FrontChannelLogoutUri = descriptor.FrontChannelLogoutUri;
        FrontChannelLogoutSessionRequired = descriptor.FrontChannelLogoutSessionRequired;
        BackChannelLogoutUri = descriptor.BackChannelLogoutUri;
        BackChannelLogoutSessionRequired = descriptor.BackChannelLogoutSessionRequired;
        DefaultMaxAge = descriptor.DefaultMaxAge;
        RequireAuthTime = descriptor.RequireAuthTime;
        ClientIdIssuedAt = descriptor.ClientIdIssuedAt;
        ClientSecretExpiresAt = descriptor.ClientSecretExpiresAt;
        ClientUri = descriptor.ClientUri;
        LogoUri = descriptor.LogoUri;
    }

    /// <summary>
    /// Gets the registered client identifier. Alias of
    /// <see cref="ProtocolMetadata.EntityId" />.
    /// </summary>
    public string ClientId => EntityId;

    /// <summary>
    /// Gets the registered redirect URIs, as exact wire strings compared ordinally.
    /// </summary>
    public IReadOnlyList<string> RedirectUris { get; }

    /// <summary>
    /// Gets the registered post-logout redirect URIs.
    /// </summary>
    public IReadOnlyList<string> PostLogoutRedirectUris { get; }

    /// <summary>
    /// Gets the response types the client will use.
    /// </summary>
    public IReadOnlyList<string> ResponseTypes { get; }

    /// <summary>
    /// Gets the grant types the client will use.
    /// </summary>
    public IReadOnlyList<string> GrantTypes { get; }

    /// <summary>
    /// Gets the scopes the client will request.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>
    /// Gets the registration contacts.
    /// </summary>
    public IReadOnlyList<string> Contacts { get; }

    /// <summary>
    /// Gets the application type (<c>web</c> or <c>native</c>).
    /// </summary>
    public string? ApplicationType { get; }

    /// <summary>
    /// Gets the human-readable client name.
    /// </summary>
    public string? ClientName { get; }

    /// <summary>
    /// Gets the token endpoint authentication method.
    /// </summary>
    public string? TokenEndpointAuthMethod { get; }

    /// <summary>
    /// Gets the subject identifier type (<c>public</c> or <c>pairwise</c>).
    /// </summary>
    public string? SubjectType { get; }

    /// <summary>
    /// Gets the sector identifier URI for pairwise subjects.
    /// </summary>
    public string? SectorIdentifierUri { get; }

    /// <summary>
    /// Gets the required ID token signing algorithm.
    /// </summary>
    public string? IdTokenSignedResponseAlg { get; }

    /// <summary>
    /// Gets the client's JWK Set document URL.
    /// </summary>
    public string? JwksUri { get; }

    /// <summary>
    /// Gets the client's front-channel logout URI.
    /// </summary>
    public string? FrontChannelLogoutUri { get; }

    /// <summary>
    /// Gets whether the front-channel logout URI needs the session identifier.
    /// </summary>
    public bool? FrontChannelLogoutSessionRequired { get; }

    /// <summary>
    /// Gets the client's back-channel logout URI.
    /// </summary>
    public string? BackChannelLogoutUri { get; }

    /// <summary>
    /// Gets whether logout tokens for this client need a session identifier.
    /// </summary>
    public bool? BackChannelLogoutSessionRequired { get; }

    /// <summary>
    /// Gets the default maximum authentication age in seconds.
    /// </summary>
    public long? DefaultMaxAge { get; }

    /// <summary>
    /// Gets whether ID tokens for this client must carry <c>auth_time</c>.
    /// </summary>
    public bool? RequireAuthTime { get; }

    /// <summary>
    /// Gets the instant the client identifier was issued.
    /// </summary>
    public DateTimeOffset? ClientIdIssuedAt { get; }

    /// <summary>
    /// Gets the instant the client secret expires; null means no expiry was declared or
    /// the secret never expires.
    /// </summary>
    public DateTimeOffset? ClientSecretExpiresAt { get; }

    /// <summary>
    /// Gets the client home page.
    /// </summary>
    public string? ClientUri { get; }

    /// <summary>
    /// Gets the client logo URL.
    /// </summary>
    public string? LogoUri { get; }

    /// <summary>
    /// Validates the record against registration conformance rules: redirect URI presence
    /// for redirect-based grants, redirect URI well-formedness, JWK source exclusivity
    /// (RFC 7591 prohibits both <c>jwks</c> and <c>jwks_uri</c>), and pairwise sector
    /// consistency.
    /// </summary>
    /// <returns>The validation findings.</returns>
    public ProtocolValidationResult Validate()
    {
        var diagnostics = new List<ProtocolValidationDiagnostic>();

        var usesRedirectGrant = GrantTypes.Count == 0; // absent grant_types defaults to authorization_code
        foreach (var grantType in GrantTypes)
        {
            usesRedirectGrant |= string.Equals(grantType, OpenIdConnectGrantTypes.AuthorizationCode, StringComparison.Ordinal)
                || string.Equals(grantType, OpenIdConnectGrantTypes.Implicit, StringComparison.Ordinal);
        }

        if (usesRedirectGrant && RedirectUris.Count == 0)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.MissingRequiredMember,
                "A client using a redirect-based grant must register at least one redirect URI.",
                member: "redirect_uris"));
        }

        foreach (var redirectUri in RedirectUris)
        {
            if (!ProtocolEndpoint.IsValidLocation(redirectUri))
            {
                diagnostics.Add(new ProtocolValidationDiagnostic(
                    ProtocolValidationSeverity.Error,
                    ProtocolValidationCodes.InvalidEndpoint,
                    $"The redirect URI '{redirectUri}' is not an absolute URI.",
                    member: "redirect_uris"));
            }
        }

        if (JwksUri is not null && Properties.ContainsKey("jwks"))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.JwksConflict,
                "A client must not declare both jwks and jwks_uri.",
                member: "jwks_uri"));
        }

        if (string.Equals(SubjectType, SubjectIdentifierFormats.Pairwise, StringComparison.Ordinal)
            && SectorIdentifierUri is null
            && CountDistinctRedirectHosts() > 1)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.SectorIdentifierInvalid,
                "A pairwise client with redirect URIs on multiple hosts must declare a sector identifier URI.",
                member: "sector_identifier_uri"));
        }

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }

    private int CountDistinctRedirectHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var redirectUri in RedirectUris)
        {
            if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            {
                hosts.Add(uri.Host);
            }
        }

        return hosts.Count;
    }

    private static ProtocolMetadataDescriptor PrepareBase(OpenIdConnectClientMetadataDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var merged = new MergedDescriptor
        {
            EntityId = descriptor.EntityId,
            ValidUntil = descriptor.ValidUntil,
            CacheDuration = descriptor.CacheDuration,
            RawDocument = descriptor.RawDocument,
        };

        foreach (var (name, value) in descriptor.Properties)
        {
            merged.Properties[name] = value;
        }

        if (descriptor.Roles.Count == 0)
        {
            merged.Roles.Add(ProtocolRole.RelyingParty);
        }
        else
        {
            foreach (var role in descriptor.Roles)
            {
                merged.Roles.Add(role);
            }
        }

        foreach (var key in descriptor.Keys)
        {
            merged.Keys.Add(key);
        }

        ProjectEndpoint(merged, OpenIdConnectEndpointKinds.FrontChannelLogout, descriptor.FrontChannelLogoutUri, ProtocolBinding.HttpRedirect);
        ProjectEndpoint(merged, OpenIdConnectEndpointKinds.BackChannelLogout, descriptor.BackChannelLogoutUri, ProtocolBinding.BackChannel);
        ProjectEndpoint(merged, OpenIdConnectEndpointKinds.Jwks, descriptor.JwksUri, ProtocolBinding.BackChannel);

        foreach (var endpoint in descriptor.Endpoints)
        {
            if (endpoint is not null &&
                (endpoint.Kind == OpenIdConnectEndpointKinds.FrontChannelLogout
                    || endpoint.Kind == OpenIdConnectEndpointKinds.BackChannelLogout
                    || endpoint.Kind == OpenIdConnectEndpointKinds.Jwks))
            {
                throw new IdentityModelException(
                    $"The extension endpoint kind '{endpoint.Kind}' collides with a typed client metadata member. " +
                    "Set the typed member instead.");
            }

            merged.Endpoints.Add(endpoint!);
        }

        return merged;
    }

    private static void ProjectEndpoint(
        MergedDescriptor merged,
        ProtocolEndpointKind kind,
        string? location,
        ProtocolBinding binding)
    {
        if (location is null || !ProtocolEndpoint.IsValidLocation(location))
        {
            return;
        }

        merged.Endpoints.Add(new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Kind = kind,
            Location = location,
            Binding = binding,
        }));
    }

    private sealed class MergedDescriptor : ProtocolMetadataDescriptor
    {
    }
}
