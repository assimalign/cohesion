using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the contents of a registered client record before it is materialized into an
/// immutable <see cref="OpenIdConnectClientMetadata" />.
/// </summary>
public class OpenIdConnectClientMetadataDescriptor : ProtocolMetadataDescriptor
{
    /// <summary>
    /// Gets or sets the registered client identifier. Alias of
    /// <see cref="ProtocolMetadataDescriptor.EntityId" />.
    /// </summary>
    public string? ClientId
    {
        get => EntityId;
        set => EntityId = value;
    }

    /// <summary>
    /// Gets the registered redirect URIs (<c>redirect_uris</c>), as exact wire strings.
    /// </summary>
    public IList<string> RedirectUris { get; } = new List<string>();

    /// <summary>
    /// Gets the registered post-logout redirect URIs (<c>post_logout_redirect_uris</c>).
    /// </summary>
    public IList<string> PostLogoutRedirectUris { get; } = new List<string>();

    /// <summary>
    /// Gets the response types the client will use (<c>response_types</c>).
    /// </summary>
    public IList<string> ResponseTypes { get; } = new List<string>();

    /// <summary>
    /// Gets the grant types the client will use (<c>grant_types</c>).
    /// </summary>
    public IList<string> GrantTypes { get; } = new List<string>();

    /// <summary>
    /// Gets the scopes the client will request (<c>scope</c>, space-delimited on the wire).
    /// </summary>
    public IList<string> Scopes { get; } = new List<string>();

    /// <summary>
    /// Gets the registration contacts (<c>contacts</c>).
    /// </summary>
    public IList<string> Contacts { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the application type (<c>application_type</c>: <c>web</c> or
    /// <c>native</c>).
    /// </summary>
    public string? ApplicationType { get; set; }

    /// <summary>
    /// Gets or sets the human-readable client name (<c>client_name</c>).
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// Gets or sets the token endpoint authentication method
    /// (<c>token_endpoint_auth_method</c>).
    /// </summary>
    public string? TokenEndpointAuthMethod { get; set; }

    /// <summary>
    /// Gets or sets the subject identifier type (<c>subject_type</c>: <c>public</c> or
    /// <c>pairwise</c>).
    /// </summary>
    public string? SubjectType { get; set; }

    /// <summary>
    /// Gets or sets the sector identifier URI for pairwise subjects
    /// (<c>sector_identifier_uri</c>).
    /// </summary>
    public string? SectorIdentifierUri { get; set; }

    /// <summary>
    /// Gets or sets the required ID token signing algorithm
    /// (<c>id_token_signed_response_alg</c>).
    /// </summary>
    public string? IdTokenSignedResponseAlg { get; set; }

    /// <summary>
    /// Gets or sets the client's JWK Set document URL (<c>jwks_uri</c>).
    /// </summary>
    public string? JwksUri { get; set; }

    /// <summary>
    /// Gets or sets the client's front-channel logout URI
    /// (<c>frontchannel_logout_uri</c>).
    /// </summary>
    public string? FrontChannelLogoutUri { get; set; }

    /// <summary>
    /// Gets or sets whether the front-channel logout URI needs the session identifier
    /// (<c>frontchannel_logout_session_required</c>).
    /// </summary>
    public bool? FrontChannelLogoutSessionRequired { get; set; }

    /// <summary>
    /// Gets or sets the client's back-channel logout URI (<c>backchannel_logout_uri</c>).
    /// </summary>
    public string? BackChannelLogoutUri { get; set; }

    /// <summary>
    /// Gets or sets whether logout tokens for this client need a session identifier
    /// (<c>backchannel_logout_session_required</c>).
    /// </summary>
    public bool? BackChannelLogoutSessionRequired { get; set; }

    /// <summary>
    /// Gets or sets the default maximum authentication age in seconds
    /// (<c>default_max_age</c>).
    /// </summary>
    public long? DefaultMaxAge { get; set; }

    /// <summary>
    /// Gets or sets whether ID tokens for this client must carry <c>auth_time</c>
    /// (<c>require_auth_time</c>).
    /// </summary>
    public bool? RequireAuthTime { get; set; }

    /// <summary>
    /// Gets or sets the instant the client identifier was issued
    /// (<c>client_id_issued_at</c>).
    /// </summary>
    public DateTimeOffset? ClientIdIssuedAt { get; set; }

    /// <summary>
    /// Gets or sets the instant the client secret expires
    /// (<c>client_secret_expires_at</c>). The wire value <c>0</c> ("never expires") maps
    /// to null. The secret itself is never modeled — only its lifecycle metadata.
    /// </summary>
    public DateTimeOffset? ClientSecretExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the client home page (<c>client_uri</c>).
    /// </summary>
    public string? ClientUri { get; set; }

    /// <summary>
    /// Gets or sets the client logo URL (<c>logo_uri</c>).
    /// </summary>
    public string? LogoUri { get; set; }
}
