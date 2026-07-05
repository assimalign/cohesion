using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents an OpenID Provider's published metadata (an OpenID Connect Discovery 1.0
/// document). The typed members store the exact wire values; the inherited base endpoint
/// list is the well-formed projection of those values, so protocol-neutral consumers can
/// enumerate endpoints while wire-fidelity consumers read the typed members.
/// </summary>
/// <remarks>
/// A typed endpoint value that is not a valid absolute URI is preserved on its typed
/// member but omitted from the base list; <see cref="Validate" /> reports it. Spec
/// conformance (the Discovery §3 REQUIRED member set, RS256 support, issuer shape) is
/// deliberately not enforced at materialization — a descriptive model must hold
/// non-conformant documents so compliance suites can diagnose them.
/// </remarks>
public sealed class OpenIdConnectProviderMetadata : ProtocolMetadata
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectProviderMetadata" />
    /// class by snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The provider metadata contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a capability entry is null or whitespace, or when a property name is
    /// blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no issuer, when an extension endpoint's kind
    /// collides with a typed endpoint member, or when a metadata list contains null
    /// entries.
    /// </exception>
    public OpenIdConnectProviderMetadata(OpenIdConnectProviderMetadataDescriptor descriptor)
        : base(PrepareBase(descriptor), AuthenticationProtocol.OpenIdConnect)
    {
        AuthorizationEndpoint = descriptor.AuthorizationEndpoint;
        TokenEndpoint = descriptor.TokenEndpoint;
        UserInfoEndpoint = descriptor.UserInfoEndpoint;
        JwksUri = descriptor.JwksUri;
        RegistrationEndpoint = descriptor.RegistrationEndpoint;
        EndSessionEndpoint = descriptor.EndSessionEndpoint;
        ScopesSupported = ModelSnapshot.Strings(descriptor.ScopesSupported, nameof(descriptor));
        ResponseTypesSupported = ModelSnapshot.Strings(descriptor.ResponseTypesSupported, nameof(descriptor));
        ResponseModesSupported = ModelSnapshot.Strings(descriptor.ResponseModesSupported, nameof(descriptor));
        GrantTypesSupported = ModelSnapshot.Strings(descriptor.GrantTypesSupported, nameof(descriptor));
        SubjectTypesSupported = ModelSnapshot.Strings(descriptor.SubjectTypesSupported, nameof(descriptor));
        IdTokenSigningAlgValuesSupported = ModelSnapshot.Strings(descriptor.IdTokenSigningAlgValuesSupported, nameof(descriptor));
        ClaimsSupported = ModelSnapshot.Strings(descriptor.ClaimsSupported, nameof(descriptor));
        CodeChallengeMethodsSupported = ModelSnapshot.Strings(descriptor.CodeChallengeMethodsSupported, nameof(descriptor));
        TokenEndpointAuthMethodsSupported = ModelSnapshot.Strings(descriptor.TokenEndpointAuthMethodsSupported, nameof(descriptor));
        AcrValuesSupported = ModelSnapshot.Strings(descriptor.AcrValuesSupported, nameof(descriptor));
        FrontChannelLogoutSupported = descriptor.FrontChannelLogoutSupported;
        FrontChannelLogoutSessionSupported = descriptor.FrontChannelLogoutSessionSupported;
        BackChannelLogoutSupported = descriptor.BackChannelLogoutSupported;
        BackChannelLogoutSessionSupported = descriptor.BackChannelLogoutSessionSupported;
        ClaimsParameterSupported = descriptor.ClaimsParameterSupported;
        RequestParameterSupported = descriptor.RequestParameterSupported;
        RequestUriParameterSupported = descriptor.RequestUriParameterSupported;
        AuthorizationResponseIssParameterSupported = descriptor.AuthorizationResponseIssParameterSupported;
    }

    /// <summary>
    /// Gets the issuer identifier. Alias of <see cref="ProtocolMetadata.EntityId" />.
    /// </summary>
    public string Issuer => EntityId;

    /// <summary>
    /// Gets the authorization endpoint URL, as the exact wire string.
    /// </summary>
    public string? AuthorizationEndpoint { get; }

    /// <summary>
    /// Gets the token endpoint URL, as the exact wire string.
    /// </summary>
    public string? TokenEndpoint { get; }

    /// <summary>
    /// Gets the UserInfo endpoint URL, as the exact wire string.
    /// </summary>
    public string? UserInfoEndpoint { get; }

    /// <summary>
    /// Gets the JWK Set document URL, as the exact wire string.
    /// </summary>
    public string? JwksUri { get; }

    /// <summary>
    /// Gets the dynamic client registration endpoint URL, as the exact wire string.
    /// </summary>
    public string? RegistrationEndpoint { get; }

    /// <summary>
    /// Gets the RP-initiated logout endpoint URL, as the exact wire string.
    /// </summary>
    public string? EndSessionEndpoint { get; }

    /// <summary>
    /// Gets the supported scope values.
    /// </summary>
    public IReadOnlyList<string> ScopesSupported { get; }

    /// <summary>
    /// Gets the supported response type values.
    /// </summary>
    public IReadOnlyList<string> ResponseTypesSupported { get; }

    /// <summary>
    /// Gets the supported response mode values.
    /// </summary>
    public IReadOnlyList<string> ResponseModesSupported { get; }

    /// <summary>
    /// Gets the supported grant type values.
    /// </summary>
    public IReadOnlyList<string> GrantTypesSupported { get; }

    /// <summary>
    /// Gets the supported subject identifier types.
    /// </summary>
    public IReadOnlyList<string> SubjectTypesSupported { get; }

    /// <summary>
    /// Gets the supported ID token signing algorithms.
    /// </summary>
    public IReadOnlyList<string> IdTokenSigningAlgValuesSupported { get; }

    /// <summary>
    /// Gets the supported claim names.
    /// </summary>
    public IReadOnlyList<string> ClaimsSupported { get; }

    /// <summary>
    /// Gets the supported PKCE code challenge methods.
    /// </summary>
    public IReadOnlyList<string> CodeChallengeMethodsSupported { get; }

    /// <summary>
    /// Gets the supported token endpoint authentication methods.
    /// </summary>
    public IReadOnlyList<string> TokenEndpointAuthMethodsSupported { get; }

    /// <summary>
    /// Gets the supported authentication context class references.
    /// </summary>
    public IReadOnlyList<string> AcrValuesSupported { get; }

    /// <summary>
    /// Gets whether front-channel logout is supported; null when the wire member was
    /// absent.
    /// </summary>
    public bool? FrontChannelLogoutSupported { get; }

    /// <summary>
    /// Gets whether front-channel logout receives a session identifier.
    /// </summary>
    public bool? FrontChannelLogoutSessionSupported { get; }

    /// <summary>
    /// Gets whether back-channel logout is supported.
    /// </summary>
    public bool? BackChannelLogoutSupported { get; }

    /// <summary>
    /// Gets whether back-channel logout tokens carry a session identifier.
    /// </summary>
    public bool? BackChannelLogoutSessionSupported { get; }

    /// <summary>
    /// Gets whether the <c>claims</c> request parameter is supported.
    /// </summary>
    public bool? ClaimsParameterSupported { get; }

    /// <summary>
    /// Gets whether the <c>request</c> parameter is supported.
    /// </summary>
    public bool? RequestParameterSupported { get; }

    /// <summary>
    /// Gets whether the <c>request_uri</c> parameter is supported.
    /// </summary>
    public bool? RequestUriParameterSupported { get; }

    /// <summary>
    /// Gets whether authorization responses carry the RFC 9207 <c>iss</c> parameter.
    /// </summary>
    public bool? AuthorizationResponseIssParameterSupported { get; }

    /// <summary>
    /// Validates the document against the OpenID Connect Discovery 1.0 conformance rules:
    /// the §3 REQUIRED member set (with <c>token_endpoint</c> conditioned on non-implicit
    /// grants), RS256 inclusion, issuer shape, and endpoint well-formedness. Recommended
    /// members produce warnings.
    /// </summary>
    /// <param name="expectedIssuer">
    /// The issuer the document was retrieved for, when known; a mismatch is an error
    /// (Discovery §4.3).
    /// </param>
    /// <returns>The validation findings.</returns>
    public ProtocolValidationResult Validate(string? expectedIssuer = null)
    {
        var diagnostics = new List<ProtocolValidationDiagnostic>();

        ValidateIssuer(diagnostics, expectedIssuer);
        ValidateRequiredEndpoints(diagnostics);
        ValidateCapabilities(diagnostics);
        ValidateEndpointShape(diagnostics, "authorization_endpoint", AuthorizationEndpoint);
        ValidateEndpointShape(diagnostics, "token_endpoint", TokenEndpoint);
        ValidateEndpointShape(diagnostics, "userinfo_endpoint", UserInfoEndpoint);
        ValidateEndpointShape(diagnostics, "jwks_uri", JwksUri);
        ValidateEndpointShape(diagnostics, "registration_endpoint", RegistrationEndpoint);
        ValidateEndpointShape(diagnostics, "end_session_endpoint", EndSessionEndpoint);

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }

    private void ValidateIssuer(List<ProtocolValidationDiagnostic> diagnostics, string? expectedIssuer)
    {
        if (expectedIssuer is not null && !string.Equals(EntityId, expectedIssuer, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.IssuerMismatch,
                "The issuer does not match the issuer the document was retrieved for.",
                member: "issuer"));
        }

        if (!Uri.TryCreate(EntityId, UriKind.Absolute, out var issuerUri)
            || !string.Equals(issuerUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(issuerUri.Query)
            || !string.IsNullOrEmpty(issuerUri.Fragment))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.ValueNotAllowed,
                "The issuer must be an https URL without query or fragment components.",
                member: "issuer"));
        }
    }

    private void ValidateRequiredEndpoints(List<ProtocolValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(AuthorizationEndpoint))
        {
            diagnostics.Add(MissingMember("authorization_endpoint"));
        }

        if (string.IsNullOrEmpty(JwksUri))
        {
            diagnostics.Add(MissingMember("jwks_uri"));
        }

        // token_endpoint is REQUIRED unless only the implicit flow is used.
        var implicitOnly = GrantTypesSupported.Count > 0;
        foreach (var grantType in GrantTypesSupported)
        {
            implicitOnly &= string.Equals(grantType, OpenIdConnectGrantTypes.Implicit, StringComparison.Ordinal);
        }

        if (string.IsNullOrEmpty(TokenEndpoint) && !implicitOnly)
        {
            diagnostics.Add(MissingMember("token_endpoint"));
        }

        if (string.IsNullOrEmpty(UserInfoEndpoint))
        {
            diagnostics.Add(RecommendedMember("userinfo_endpoint"));
        }

        if (string.IsNullOrEmpty(RegistrationEndpoint))
        {
            diagnostics.Add(RecommendedMember("registration_endpoint"));
        }
    }

    private void ValidateCapabilities(List<ProtocolValidationDiagnostic> diagnostics)
    {
        if (ResponseTypesSupported.Count == 0)
        {
            diagnostics.Add(MissingMember("response_types_supported"));
        }

        if (SubjectTypesSupported.Count == 0)
        {
            diagnostics.Add(MissingMember("subject_types_supported"));
        }

        if (IdTokenSigningAlgValuesSupported.Count == 0)
        {
            diagnostics.Add(MissingMember("id_token_signing_alg_values_supported"));
        }
        else
        {
            var hasRs256 = false;
            foreach (var algorithm in IdTokenSigningAlgValuesSupported)
            {
                hasRs256 |= string.Equals(algorithm, "RS256", StringComparison.Ordinal);
            }

            if (!hasRs256)
            {
                diagnostics.Add(new ProtocolValidationDiagnostic(
                    ProtocolValidationSeverity.Error,
                    OpenIdConnectValidationCodes.Rs256NotSupported,
                    "Discovery requires RS256 among the supported ID token signing algorithms.",
                    member: "id_token_signing_alg_values_supported"));
            }
        }

        if (ScopesSupported.Count == 0)
        {
            diagnostics.Add(RecommendedMember("scopes_supported"));
        }
        else
        {
            var hasOpenId = false;
            foreach (var scope in ScopesSupported)
            {
                hasOpenId |= string.Equals(scope, OpenIdConnectScopes.OpenId, StringComparison.Ordinal);
            }

            if (!hasOpenId)
            {
                diagnostics.Add(new ProtocolValidationDiagnostic(
                    ProtocolValidationSeverity.Warning,
                    OpenIdConnectValidationCodes.MissingOpenIdScope,
                    "The advertised scopes do not include 'openid'.",
                    member: "scopes_supported"));
            }
        }

        if (ClaimsSupported.Count == 0)
        {
            diagnostics.Add(RecommendedMember("claims_supported"));
        }
    }

    private static void ValidateEndpointShape(
        List<ProtocolValidationDiagnostic> diagnostics,
        string member,
        string? value)
    {
        if (value is not null && !ProtocolEndpoint.IsValidLocation(value))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.InvalidEndpoint,
                $"The {member} value is not an absolute URI.",
                member: member));
        }
    }

    private static ProtocolValidationDiagnostic MissingMember(string member)
        => new(
            ProtocolValidationSeverity.Error,
            ProtocolValidationCodes.MissingRequiredMember,
            $"The provider metadata is missing the required {member} member.",
            member: member);

    private static ProtocolValidationDiagnostic RecommendedMember(string member)
        => new(
            ProtocolValidationSeverity.Warning,
            ProtocolValidationCodes.MissingRecommendedMember,
            $"The provider metadata is missing the recommended {member} member.",
            member: member);

    private static ProtocolMetadataDescriptor PrepareBase(OpenIdConnectProviderMetadataDescriptor descriptor)
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
            merged.Roles.Add(ProtocolRole.IdentityProvider);
            merged.Roles.Add(ProtocolRole.AuthorizationServer);
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

        ProjectEndpoint(merged, OpenIdConnectEndpointKinds.Authorization, descriptor.AuthorizationEndpoint, ProtocolBinding.HttpRedirect);
        ProjectEndpoint(merged, OpenIdConnectEndpointKinds.Token, descriptor.TokenEndpoint, ProtocolBinding.BackChannel);
        ProjectEndpoint(merged, OpenIdConnectEndpointKinds.UserInfo, descriptor.UserInfoEndpoint, ProtocolBinding.BackChannel);
        ProjectEndpoint(merged, OpenIdConnectEndpointKinds.Jwks, descriptor.JwksUri, ProtocolBinding.BackChannel);
        ProjectEndpoint(merged, OpenIdConnectEndpointKinds.Registration, descriptor.RegistrationEndpoint, ProtocolBinding.BackChannel);
        ProjectEndpoint(merged, OpenIdConnectEndpointKinds.EndSession, descriptor.EndSessionEndpoint, ProtocolBinding.HttpRedirect);

        foreach (var endpoint in descriptor.Endpoints)
        {
            if (endpoint is not null && IsTypedKind(endpoint.Kind))
            {
                throw new IdentityModelException(
                    $"The extension endpoint kind '{endpoint.Kind}' collides with a typed provider metadata member. " +
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
        // Malformed wire values stay on the typed member only; Validate() reports them.
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

    private static bool IsTypedKind(ProtocolEndpointKind kind)
    {
        return kind == OpenIdConnectEndpointKinds.Authorization
            || kind == OpenIdConnectEndpointKinds.Token
            || kind == OpenIdConnectEndpointKinds.UserInfo
            || kind == OpenIdConnectEndpointKinds.Jwks
            || kind == OpenIdConnectEndpointKinds.Registration
            || kind == OpenIdConnectEndpointKinds.EndSession;
    }

    private sealed class MergedDescriptor : ProtocolMetadataDescriptor
    {
    }
}
