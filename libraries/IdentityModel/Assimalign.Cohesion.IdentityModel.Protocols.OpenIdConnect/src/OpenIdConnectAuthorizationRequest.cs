using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents an OpenID Connect authentication (authorization) request. Authorization
/// Code with PKCE is the primary modeled flow; implicit-era shapes are representable for
/// compatibility. The OAuth <c>state</c> parameter rides the inherited
/// <see cref="ProtocolMessage.CorrelationState" />.
/// </summary>
public sealed class OpenIdConnectAuthorizationRequest : ProtocolRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectAuthorizationRequest" />
    /// class by snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The request contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a list entry is null or whitespace, or when a property name is blank
    /// or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no client identifier or no response type — the
    /// members that make the artifact recognizable as an authorization request. All spec
    /// conformance beyond that is reported by <see cref="Validate" />.
    /// </exception>
    public OpenIdConnectAuthorizationRequest(OpenIdConnectAuthorizationRequestDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.OpenIdConnect)
    {
        if (string.IsNullOrWhiteSpace(descriptor.ClientId))
        {
            throw new IdentityModelException("An authorization request requires a client identifier.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.ResponseType))
        {
            throw new IdentityModelException("An authorization request requires a response type.");
        }

        ResponseType = descriptor.ResponseType;
        ResponseTypeParts = new System.Collections.ObjectModel.ReadOnlyCollection<string>(
            OpenIdConnectResponseTypes.Split(descriptor.ResponseType));
        RedirectUri = descriptor.RedirectUri;
        Scopes = ModelSnapshot.Strings(descriptor.Scopes, nameof(descriptor));
        Nonce = descriptor.Nonce;
        CodeChallenge = descriptor.CodeChallenge;
        CodeChallengeMethod = descriptor.CodeChallengeMethod;
        ResponseMode = descriptor.ResponseMode;
        Prompts = ModelSnapshot.Strings(descriptor.Prompts, nameof(descriptor));
        MaxAge = descriptor.MaxAge;
        LoginHint = descriptor.LoginHint;
        IdTokenHint = descriptor.IdTokenHint;
        AcrValues = ModelSnapshot.Strings(descriptor.AcrValues, nameof(descriptor));
        Display = descriptor.Display;
        UiLocales = ModelSnapshot.Strings(descriptor.UiLocales, nameof(descriptor));
        ClaimsRequest = descriptor.ClaimsRequest;
        Request = descriptor.Request;
        RequestUri = descriptor.RequestUri;
    }

    /// <summary>
    /// Gets the client identifier. Alias of the base envelope's
    /// <see cref="ProtocolMessage.Issuer" /> (the client is the request's sender).
    /// </summary>
    public string ClientId => Issuer!;

    /// <summary>
    /// Gets the response type, as the exact wire string. Compare with
    /// <see cref="OpenIdConnectResponseTypes.Matches" />, never ordinally.
    /// </summary>
    public string ResponseType { get; }

    /// <summary>
    /// Gets the response type's atomic parts, in wire order.
    /// </summary>
    public IReadOnlyList<string> ResponseTypeParts { get; }

    /// <summary>
    /// Gets the redirect URI, as the exact wire string compared ordinally.
    /// </summary>
    public string? RedirectUri { get; }

    /// <summary>
    /// Gets the requested scopes.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>
    /// Gets the replay-prevention nonce.
    /// </summary>
    public string? Nonce { get; }

    /// <summary>
    /// Gets the PKCE code challenge.
    /// </summary>
    public string? CodeChallenge { get; }

    /// <summary>
    /// Gets the PKCE code challenge method.
    /// </summary>
    public string? CodeChallengeMethod { get; }

    /// <summary>
    /// Gets the response mode.
    /// </summary>
    public string? ResponseMode { get; }

    /// <summary>
    /// Gets the prompt values.
    /// </summary>
    public IReadOnlyList<string> Prompts { get; }

    /// <summary>
    /// Gets the maximum authentication age in seconds.
    /// </summary>
    public long? MaxAge { get; }

    /// <summary>
    /// Gets the login hint.
    /// </summary>
    public string? LoginHint { get; }

    /// <summary>
    /// Gets the ID token hint, as the original compact token string.
    /// </summary>
    public string? IdTokenHint { get; }

    /// <summary>
    /// Gets the requested authentication context class references.
    /// </summary>
    public IReadOnlyList<string> AcrValues { get; }

    /// <summary>
    /// Gets the display preference.
    /// </summary>
    public string? Display { get; }

    /// <summary>
    /// Gets the preferred UI locales.
    /// </summary>
    public IReadOnlyList<string> UiLocales { get; }

    /// <summary>
    /// Gets the <c>claims</c> request parameter as raw JSON text.
    /// </summary>
    public string? ClaimsRequest { get; }

    /// <summary>
    /// Gets the <c>request</c> parameter (RFC 9101 request object), as the raw compact
    /// JWT.
    /// </summary>
    public string? Request { get; }

    /// <summary>
    /// Gets the <c>request_uri</c> parameter (RFC 9101).
    /// </summary>
    public string? RequestUri { get; }

    /// <summary>
    /// Validates the request against OpenID Connect Core and current-practice rules: the
    /// <c>openid</c> scope, redirect URI presence and shape, nonce requiredness for
    /// implicit and hybrid response types, PKCE presence and method (a best-current-
    /// practice warning), and prompt combination rules.
    /// </summary>
    /// <returns>The validation findings.</returns>
    public ProtocolValidationResult Validate()
    {
        var diagnostics = new List<ProtocolValidationDiagnostic>();

        var hasOpenIdScope = false;
        foreach (var scope in Scopes)
        {
            hasOpenIdScope |= string.Equals(scope, OpenIdConnectScopes.OpenId, StringComparison.Ordinal);
        }

        if (!hasOpenIdScope)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.MissingOpenIdScope,
                "An OpenID Connect authentication request must include the 'openid' scope.",
                member: "scope"));
        }

        if (RedirectUri is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.MissingRequiredMember,
                "An OpenID Connect authentication request must carry a redirect URI.",
                member: "redirect_uri"));
        }
        else if (!ProtocolEndpoint.IsValidLocation(RedirectUri))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.InvalidEndpoint,
                "The redirect URI is not an absolute URI.",
                member: "redirect_uri"));
        }

        // Implicit and hybrid response types require a nonce (Core §3.2.2.1 / §3.3.2.11).
        var issuesIdTokenFromAuthorization = false;
        foreach (var part in ResponseTypeParts)
        {
            issuesIdTokenFromAuthorization |= string.Equals(part, OpenIdConnectResponseTypes.IdToken, StringComparison.Ordinal);
        }

        if (issuesIdTokenFromAuthorization && Nonce is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.NonceMissing,
                "Response types that issue an ID token from the authorization endpoint require a nonce.",
                member: "nonce"));
        }

        // PKCE with S256 is best current practice for the code flow.
        var requestsCode = false;
        foreach (var part in ResponseTypeParts)
        {
            requestsCode |= string.Equals(part, OpenIdConnectResponseTypes.Code, StringComparison.Ordinal);
        }

        if (requestsCode &&
            (CodeChallenge is null || !string.Equals(CodeChallengeMethod, "S256", StringComparison.Ordinal)))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Warning,
                OpenIdConnectValidationCodes.PkceMissing,
                "Authorization code requests should carry a PKCE code challenge with the S256 method.",
                member: "code_challenge"));
        }

        var hasPromptNone = false;
        foreach (var prompt in Prompts)
        {
            hasPromptNone |= string.Equals(prompt, OpenIdConnectPromptValues.None, StringComparison.Ordinal);
        }

        if (hasPromptNone && Prompts.Count > 1)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.PromptNoneCombined,
                "prompt=none must not be combined with other prompt values.",
                member: "prompt"));
        }

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }
}
