using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents an authorization response. The inherited
/// <see cref="ProtocolMessage.Issuer" /> carries the RFC 9207 <c>iss</c> response
/// parameter when present, the inherited <see cref="ProtocolMessage.CorrelationState" />
/// carries the <c>state</c> echo, and the inherited
/// <see cref="ProtocolResponse.Status" /> carries the outcome. Spec conformance —
/// including whether the artifacts promised by the requested response type are present —
/// is reported by <see cref="Validate" />, never enforced at materialization, so
/// non-conformant wire responses stay diagnosable (and <c>response_type=none</c> success
/// responses, which legally carry no artifact, stay representable).
/// </summary>
public sealed class OpenIdConnectAuthorizationResponse : ProtocolResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectAuthorizationResponse" />
    /// class by snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The response contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a scope entry is null or whitespace, or when a property name is blank
    /// or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">Thrown when the descriptor has no status.</exception>
    public OpenIdConnectAuthorizationResponse(OpenIdConnectAuthorizationResponseDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.OpenIdConnect)
    {
        Code = descriptor.Code;
        IdToken = descriptor.IdToken;
        AccessToken = descriptor.AccessToken;
        TokenType = descriptor.TokenType;
        ExpiresIn = descriptor.ExpiresIn;
        Scopes = ModelSnapshot.Strings(descriptor.Scopes, nameof(descriptor));
    }

    /// <summary>
    /// Gets the authorization code, when issued.
    /// </summary>
    public string? Code { get; }

    /// <summary>
    /// Gets the ID token, as the raw compact string, when issued from the authorization
    /// endpoint.
    /// </summary>
    public string? IdToken { get; }

    /// <summary>
    /// Gets the access token, as the raw string, when issued from the authorization
    /// endpoint.
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
    /// Gets the granted scopes, when the server narrowed the request.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>
    /// Validates the response against the requesting context: the <c>state</c> echo, the
    /// RFC 9207 issuer parameter, and the presence of the artifacts the requested response
    /// type promises.
    /// </summary>
    /// <param name="options">The expectations from the request the response answers.</param>
    /// <returns>The validation findings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is null.</exception>
    public ProtocolValidationResult Validate(OpenIdConnectAuthorizationResponseValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<ProtocolValidationDiagnostic>();

        if (options.ExpectedCorrelationState is not null &&
            !string.Equals(CorrelationState, options.ExpectedCorrelationState, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.CorrelationMismatch,
                "The state echo does not match the request's state.",
                member: "state"));
        }

        if (options.ExpectedIssuer is not null)
        {
            if (Issuer is null)
            {
                if (options.IssuerParameterAdvertised)
                {
                    // RFC 9207 §2.4: when the provider advertises support, clients MUST
                    // reject responses without the iss parameter — this is the mix-up
                    // attack window, so the finding is an error.
                    diagnostics.Add(new ProtocolValidationDiagnostic(
                        ProtocolValidationSeverity.Error,
                        OpenIdConnectValidationCodes.IssParameterMissing,
                        "The provider advertises the iss response parameter but the response does not carry it.",
                        member: "iss"));
                }
            }
            else if (!string.Equals(Issuer, options.ExpectedIssuer, StringComparison.Ordinal))
            {
                diagnostics.Add(new ProtocolValidationDiagnostic(
                    ProtocolValidationSeverity.Error,
                    ProtocolValidationCodes.IssuerMismatch,
                    "The iss response parameter does not match the expected issuer.",
                    member: "iss"));
            }
        }

        if (Status.Succeeded && options.ExpectedResponseType is not null)
        {
            foreach (var part in OpenIdConnectResponseTypes.Split(options.ExpectedResponseType))
            {
                var missing = part switch
                {
                    OpenIdConnectResponseTypes.Code => Code is null,
                    OpenIdConnectResponseTypes.IdToken => IdToken is null,
                    OpenIdConnectResponseTypes.Token => AccessToken is null,
                    _ => false,
                };

                if (missing)
                {
                    diagnostics.Add(new ProtocolValidationDiagnostic(
                        ProtocolValidationSeverity.Error,
                        OpenIdConnectValidationCodes.MissingSuccessArtifact,
                        $"The success response is missing the '{part}' artifact the response type promises.",
                        member: part));
                }
            }
        }

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }
}
