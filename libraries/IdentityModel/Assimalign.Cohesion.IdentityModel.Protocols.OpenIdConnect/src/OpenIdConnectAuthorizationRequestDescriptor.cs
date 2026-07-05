using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the contents of an authorization request before it is materialized into an
/// immutable <see cref="OpenIdConnectAuthorizationRequest" />.
/// </summary>
public class OpenIdConnectAuthorizationRequestDescriptor : ProtocolRequestDescriptor
{
    /// <summary>
    /// Gets or sets the client identifier (<c>client_id</c>). Alias of the base envelope's
    /// <see cref="ProtocolMessageDescriptor.Issuer" /> — the client is the sender of a
    /// request. Required at materialization.
    /// </summary>
    public string? ClientId
    {
        get => Issuer;
        set => Issuer = value;
    }

    /// <summary>
    /// Gets or sets the response type (<c>response_type</c>), as the exact wire string.
    /// Required at materialization. Composite values are space-delimited and
    /// order-insensitive; compare with <see cref="OpenIdConnectResponseTypes.Matches" />.
    /// </summary>
    public string? ResponseType { get; set; }

    /// <summary>
    /// Gets or sets the redirect URI (<c>redirect_uri</c>), as the exact wire string.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// Gets the requested scopes (<c>scope</c>, space-delimited on the wire).
    /// </summary>
    public IList<string> Scopes { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the replay-prevention nonce (<c>nonce</c>).
    /// </summary>
    public string? Nonce { get; set; }

    /// <summary>
    /// Gets or sets the PKCE code challenge (<c>code_challenge</c>).
    /// </summary>
    public string? CodeChallenge { get; set; }

    /// <summary>
    /// Gets or sets the PKCE code challenge method (<c>code_challenge_method</c>).
    /// </summary>
    public string? CodeChallengeMethod { get; set; }

    /// <summary>
    /// Gets or sets the response mode (<c>response_mode</c>).
    /// </summary>
    public string? ResponseMode { get; set; }

    /// <summary>
    /// Gets the prompt values (<c>prompt</c>, space-delimited on the wire).
    /// </summary>
    public IList<string> Prompts { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the maximum authentication age in seconds (<c>max_age</c>).
    /// </summary>
    public long? MaxAge { get; set; }

    /// <summary>
    /// Gets or sets the login hint (<c>login_hint</c>).
    /// </summary>
    public string? LoginHint { get; set; }

    /// <summary>
    /// Gets or sets the ID token hint (<c>id_token_hint</c>), as the original compact
    /// token string.
    /// </summary>
    public string? IdTokenHint { get; set; }

    /// <summary>
    /// Gets the requested authentication context class references (<c>acr_values</c>,
    /// space-delimited on the wire).
    /// </summary>
    public IList<string> AcrValues { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the display preference (<c>display</c>).
    /// </summary>
    public string? Display { get; set; }

    /// <summary>
    /// Gets the preferred UI locales (<c>ui_locales</c>, space-delimited on the wire).
    /// </summary>
    public IList<string> UiLocales { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the <c>claims</c> request parameter as its raw JSON text; no object
    /// model is applied.
    /// </summary>
    public string? ClaimsRequest { get; set; }

    /// <summary>
    /// Gets or sets the <c>request</c> parameter (RFC 9101 request object), as the raw
    /// compact JWT.
    /// </summary>
    public string? Request { get; set; }

    /// <summary>
    /// Gets or sets the <c>request_uri</c> parameter (RFC 9101), as the exact wire string.
    /// </summary>
    public string? RequestUri { get; set; }
}
