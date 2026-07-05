namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the wire parameter names used by OpenID Connect and OAuth 2.0 messages, for
/// transport layers that render or parse query strings and form bodies.
/// </summary>
public static class OpenIdConnectParameterNames
{
    /// <summary>The <c>client_id</c> parameter.</summary>
    public const string ClientId = "client_id";

    /// <summary>The <c>redirect_uri</c> parameter.</summary>
    public const string RedirectUri = "redirect_uri";

    /// <summary>The <c>response_type</c> parameter.</summary>
    public const string ResponseType = "response_type";

    /// <summary>The <c>response_mode</c> parameter.</summary>
    public const string ResponseMode = "response_mode";

    /// <summary>The <c>scope</c> parameter.</summary>
    public const string Scope = "scope";

    /// <summary>The <c>state</c> parameter.</summary>
    public const string State = "state";

    /// <summary>The <c>nonce</c> parameter.</summary>
    public const string Nonce = "nonce";

    /// <summary>The <c>code_challenge</c> parameter (PKCE).</summary>
    public const string CodeChallenge = "code_challenge";

    /// <summary>The <c>code_challenge_method</c> parameter (PKCE).</summary>
    public const string CodeChallengeMethod = "code_challenge_method";

    /// <summary>The <c>code_verifier</c> parameter (PKCE).</summary>
    public const string CodeVerifier = "code_verifier";

    /// <summary>The <c>code</c> parameter.</summary>
    public const string Code = "code";

    /// <summary>The <c>error</c> parameter.</summary>
    public const string Error = "error";

    /// <summary>The <c>error_description</c> parameter.</summary>
    public const string ErrorDescription = "error_description";

    /// <summary>The <c>error_uri</c> parameter.</summary>
    public const string ErrorUri = "error_uri";

    /// <summary>The <c>id_token</c> parameter.</summary>
    public const string IdToken = "id_token";

    /// <summary>The <c>access_token</c> parameter.</summary>
    public const string AccessToken = "access_token";

    /// <summary>The <c>token_type</c> parameter.</summary>
    public const string TokenType = "token_type";

    /// <summary>The <c>expires_in</c> parameter.</summary>
    public const string ExpiresIn = "expires_in";

    /// <summary>The <c>refresh_token</c> parameter.</summary>
    public const string RefreshToken = "refresh_token";

    /// <summary>The <c>grant_type</c> parameter.</summary>
    public const string GrantType = "grant_type";

    /// <summary>The <c>client_assertion</c> parameter (RFC 7523).</summary>
    public const string ClientAssertion = "client_assertion";

    /// <summary>The <c>client_assertion_type</c> parameter (RFC 7523).</summary>
    public const string ClientAssertionType = "client_assertion_type";

    /// <summary>The <c>prompt</c> parameter.</summary>
    public const string Prompt = "prompt";

    /// <summary>The <c>max_age</c> parameter.</summary>
    public const string MaxAge = "max_age";

    /// <summary>The <c>login_hint</c> parameter.</summary>
    public const string LoginHint = "login_hint";

    /// <summary>The <c>acr_values</c> parameter.</summary>
    public const string AcrValues = "acr_values";

    /// <summary>The <c>display</c> parameter.</summary>
    public const string Display = "display";

    /// <summary>The <c>ui_locales</c> parameter.</summary>
    public const string UiLocales = "ui_locales";

    /// <summary>The <c>claims</c> parameter.</summary>
    public const string Claims = "claims";

    /// <summary>The <c>request</c> parameter (RFC 9101).</summary>
    public const string Request = "request";

    /// <summary>The <c>request_uri</c> parameter (RFC 9101).</summary>
    public const string RequestUri = "request_uri";

    /// <summary>The <c>id_token_hint</c> parameter.</summary>
    public const string IdTokenHint = "id_token_hint";

    /// <summary>The <c>logout_hint</c> parameter.</summary>
    public const string LogoutHint = "logout_hint";

    /// <summary>The <c>post_logout_redirect_uri</c> parameter.</summary>
    public const string PostLogoutRedirectUri = "post_logout_redirect_uri";

    /// <summary>The <c>logout_token</c> parameter (Back-Channel Logout 1.0).</summary>
    public const string LogoutToken = "logout_token";

    /// <summary>The <c>sid</c> parameter.</summary>
    public const string SessionId = "sid";

    /// <summary>The <c>iss</c> authorization response parameter (RFC 9207).</summary>
    public const string Issuer = "iss";
}
