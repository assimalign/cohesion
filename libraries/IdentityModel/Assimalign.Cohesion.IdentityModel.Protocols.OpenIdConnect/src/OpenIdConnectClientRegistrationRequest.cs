using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents a dynamic client registration request (RFC 7591 / OpenID Connect Dynamic
/// Client Registration 1.0): the client metadata a would-be client submits before it has
/// an identifier. Deliberately not a <see cref="ProtocolMetadata" /> derivative — the
/// family rule is that published metadata always has an entity identifier, and here the
/// server assigns <c>client_id</c> in the registration response
/// (<see cref="OpenIdConnectClientMetadata" />).
/// </summary>
/// <remarks>
/// The request reuses <see cref="OpenIdConnectClientMetadataDescriptor" /> as its input
/// shape, with the client identifier required to be absent.
/// </remarks>
public sealed class OpenIdConnectClientRegistrationRequest
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="OpenIdConnectClientRegistrationRequest" /> class by snapshotting the
    /// provided descriptor.
    /// </summary>
    /// <param name="descriptor">The requested client metadata. Its client identifier must be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a list entry is null or whitespace, or when a property name is blank
    /// or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor carries a client identifier — a registration request
    /// has none; the server assigns it.
    /// </exception>
    public OpenIdConnectClientRegistrationRequest(OpenIdConnectClientMetadataDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.ClientId is not null)
        {
            throw new IdentityModelException(
                "A registration request has no client identifier; the server assigns it in the response.");
        }

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
        ClientUri = descriptor.ClientUri;
        LogoUri = descriptor.LogoUri;
        RawDocument = descriptor.RawDocument;
        Properties = ModelSnapshot.Properties(descriptor.Properties, nameof(descriptor));
    }

    /// <summary>
    /// Gets the requested redirect URIs, as exact wire strings.
    /// </summary>
    public IReadOnlyList<string> RedirectUris { get; }

    /// <summary>
    /// Gets the requested post-logout redirect URIs.
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
    /// Gets the requested token endpoint authentication method.
    /// </summary>
    public string? TokenEndpointAuthMethod { get; }

    /// <summary>
    /// Gets the requested subject identifier type.
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
    /// Gets the client home page.
    /// </summary>
    public string? ClientUri { get; }

    /// <summary>
    /// Gets the client logo URL.
    /// </summary>
    public string? LogoUri { get; }

    /// <summary>
    /// Gets the as-received request document text, when retained.
    /// </summary>
    public string? RawDocument { get; }

    /// <summary>
    /// Gets additional requested metadata.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }
}
