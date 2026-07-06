using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents a token endpoint response. Every member is nullable by design: real-world
/// providers omit REQUIRED members (most commonly <c>token_type</c>), and a descriptive
/// model must hold such responses so <see cref="Validate" /> can diagnose them.
/// </summary>
public sealed class OpenIdConnectTokenResponse : ProtocolResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectTokenResponse" /> class
    /// by snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The response contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a scope entry is null or whitespace, or when a property name is blank
    /// or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">Thrown when the descriptor has no status.</exception>
    public OpenIdConnectTokenResponse(OpenIdConnectTokenResponseDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.OpenIdConnect)
    {
        AccessToken = descriptor.AccessToken;
        TokenType = descriptor.TokenType;
        ExpiresIn = descriptor.ExpiresIn;
        RefreshToken = descriptor.RefreshToken;
        IdToken = descriptor.IdToken;
        Scopes = ModelSnapshot.Strings(descriptor.Scopes, nameof(descriptor));
    }

    /// <summary>
    /// Gets the access token, as the raw string.
    /// </summary>
    public string? AccessToken { get; }

    /// <summary>
    /// Gets the access token type.
    /// </summary>
    public string? TokenType { get; }

    /// <summary>
    /// Gets the access token lifetime in seconds.
    /// </summary>
    public long? ExpiresIn { get; }

    /// <summary>
    /// Gets the refresh token.
    /// </summary>
    public string? RefreshToken { get; }

    /// <summary>
    /// Gets the ID token, as the raw compact string.
    /// </summary>
    public string? IdToken { get; }

    /// <summary>
    /// Gets the granted scopes, when the server narrowed the request.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>
    /// Validates a success response against RFC 6749 §5.1 (access token and token type
    /// are required) and, when the exchange was an OpenID Connect authentication,
    /// OpenID Connect Core §3.1.3.3 (an ID token is required).
    /// </summary>
    /// <param name="expectIdToken">
    /// Whether the exchange redeemed an OpenID Connect authentication (the <c>openid</c>
    /// scope was granted), making the ID token required.
    /// </param>
    /// <returns>The validation findings.</returns>
    public ProtocolValidationResult Validate(bool expectIdToken = false)
    {
        if (!Status.Succeeded)
        {
            return ProtocolValidationResult.Success;
        }

        var diagnostics = new List<ProtocolValidationDiagnostic>();

        if (AccessToken is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.MissingAccessToken,
                "A success token response requires an access token.",
                member: "access_token"));
        }

        if (TokenType is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.MissingTokenType,
                "A success token response requires a token type.",
                member: "token_type"));
        }

        if (expectIdToken && IdToken is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.MissingRequiredMember,
                "A token response redeeming an OpenID Connect authentication requires an ID token.",
                member: "id_token"));
        }

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }
}
