using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents the claims surface of a back-channel logout token (Back-Channel Logout 1.0
/// §2.4). Compact parsing and signature verification are the JSON Web Token package's
/// concern; <see cref="Validate" /> owns the §2.6 data rules.
/// </summary>
public sealed class OpenIdConnectLogoutToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectLogoutToken" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The token contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a list entry is null or whitespace, or an events member name is blank
    /// or its value undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when an extension claim's type collides with a typed member's claim name.
    /// A prohibited <c>nonce</c> is deliberately not a collision — it must stay
    /// constructible so <see cref="Validate" /> can report it.
    /// </exception>
    public OpenIdConnectLogoutToken(OpenIdConnectLogoutTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Issuer = descriptor.Issuer;
        Subject = descriptor.Subject;
        Audiences = ModelSnapshot.Strings(descriptor.Audiences, nameof(descriptor));
        IssuedAt = descriptor.IssuedAt;
        ExpiresAt = descriptor.ExpiresAt;
        JwtId = descriptor.JwtId;
        SessionId = descriptor.SessionId;
        Events = ModelSnapshot.Properties(descriptor.Events, nameof(descriptor));
        RawToken = descriptor.RawToken;
        Claims = BuildClaims(descriptor);
    }

    /// <summary>
    /// Gets the issuer (<c>iss</c>).
    /// </summary>
    public string? Issuer { get; }

    /// <summary>
    /// Gets the subject (<c>sub</c>), as the raw wire string.
    /// </summary>
    public string? Subject { get; }

    /// <summary>
    /// Gets the audiences (<c>aud</c>).
    /// </summary>
    public IReadOnlyList<string> Audiences { get; }

    /// <summary>
    /// Gets the issuance instant (<c>iat</c>).
    /// </summary>
    public DateTimeOffset? IssuedAt { get; }

    /// <summary>
    /// Gets the expiration instant (<c>exp</c>).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Gets the token identifier (<c>jti</c>).
    /// </summary>
    public string? JwtId { get; }

    /// <summary>
    /// Gets the provider session identifier (<c>sid</c>).
    /// </summary>
    public string? SessionId { get; }

    /// <summary>
    /// Gets the security events as the wire's JSON object shape: event URI to event
    /// payload.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Events { get; }

    /// <summary>
    /// Gets the original compact serialization, when retained.
    /// </summary>
    public string? RawToken { get; }

    /// <summary>
    /// Gets every claim the token asserts, with OpenID Connect provenance.
    /// </summary>
    public IIdentityClaimCollection Claims { get; }

    /// <summary>
    /// Validates the token's data rules per Back-Channel Logout 1.0 §2.4–§2.6: required
    /// claims, the back-channel logout event with an object payload, subject-or-session
    /// presence, the nonce prohibition, issuer and audience matches, and temporal windows.
    /// </summary>
    /// <param name="options">The validation expectations.</param>
    /// <returns>The validation findings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is null.</exception>
    public ProtocolValidationResult Validate(OpenIdConnectLogoutTokenValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<ProtocolValidationDiagnostic>();

        if (Issuer is null)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.Issuer));
        }
        else if (options.ExpectedIssuer is not null &&
            !string.Equals(Issuer, options.ExpectedIssuer, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.IssuerMismatch,
                "The logout token issuer does not match the expected issuer.",
                member: IdentityClaimTypes.Issuer));
        }

        if (Audiences.Count == 0)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.Audience));
        }
        else if (options.ExpectedAudience is not null)
        {
            var containsClient = false;
            foreach (var audience in Audiences)
            {
                containsClient |= string.Equals(audience, options.ExpectedAudience, StringComparison.Ordinal);
            }

            if (!containsClient)
            {
                diagnostics.Add(new ProtocolValidationDiagnostic(
                    ProtocolValidationSeverity.Error,
                    ProtocolValidationCodes.AudienceMismatch,
                    "The logout token audiences do not include the client.",
                    member: IdentityClaimTypes.Audience));
            }

            // §2.6 validates audiences the same way ID tokens do: additional audiences
            // are untrusted by default.
            if (!options.AllowAdditionalAudiences)
            {
                foreach (var audience in Audiences)
                {
                    if (string.Equals(audience, options.ExpectedAudience, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var trusted = false;
                    foreach (var candidate in options.TrustedAudiences)
                    {
                        trusted |= string.Equals(audience, candidate, StringComparison.Ordinal);
                    }

                    if (!trusted)
                    {
                        diagnostics.Add(new ProtocolValidationDiagnostic(
                            ProtocolValidationSeverity.Error,
                            ProtocolValidationCodes.AudienceMismatch,
                            $"The logout token lists an untrusted additional audience '{audience}'.",
                            member: IdentityClaimTypes.Audience));
                    }
                }
            }
        }

        if (IssuedAt is null)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.IssuedAt));
        }
        else if (IssuedAt.Value > options.ValidateAt + options.ClockSkew)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.NotYetValid,
                "The logout token is issued in the future.",
                member: IdentityClaimTypes.IssuedAt));
        }

        if (ExpiresAt is null)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.ExpirationTime));
        }
        else if (options.ValidateAt - options.ClockSkew >= ExpiresAt.Value)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.Expired,
                "The logout token is expired.",
                member: IdentityClaimTypes.ExpirationTime));
        }

        if (JwtId is null)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.JwtId));
        }

        if (!Events.TryGetValue(OpenIdConnectEventTypes.BackChannelLogout, out var payload)
            || payload.Kind != IdentityValueKind.Object)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.LogoutEventInvalid,
                "The events claim must carry the back-channel logout event with a JSON object payload.",
                member: OpenIdConnectClaimTypes.Events));
        }

        if (Subject is null && SessionId is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.LogoutSubjectMissing,
                "A logout token must identify a subject (sub) or a session (sid).",
                member: IdentityClaimTypes.Subject));
        }

        if (Claims.Contains(OpenIdConnectClaimTypes.Nonce))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.LogoutTokenNonceProhibited,
                "A logout token must not carry a nonce claim.",
                member: OpenIdConnectClaimTypes.Nonce));
        }

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }

    private static ProtocolValidationDiagnostic MissingClaim(string claim)
        => new(
            ProtocolValidationSeverity.Error,
            ProtocolValidationCodes.MissingRequiredMember,
            $"The logout token is missing the required '{claim}' claim.",
            member: claim);

    private static IIdentityClaimCollection BuildClaims(OpenIdConnectLogoutTokenDescriptor descriptor)
    {
        var provenance = new IdentityClaimProvenance(
            AuthenticationProtocol.OpenIdConnect,
            originalIssuer: descriptor.Issuer);

        var claims = new List<IIdentityClaim>();

        void AddString(string type, string? value)
        {
            if (value is not null)
            {
                claims.Add(new IdentityClaim(type, value, descriptor.Issuer, provenance));
            }
        }

        AddString(IdentityClaimTypes.Issuer, descriptor.Issuer);
        AddString(IdentityClaimTypes.Subject, descriptor.Subject);

        foreach (var audience in descriptor.Audiences)
        {
            AddString(IdentityClaimTypes.Audience, audience);
        }

        if (descriptor.IssuedAt is not null)
        {
            claims.Add(new IdentityClaim(
                IdentityClaimTypes.IssuedAt,
                IdentityClaimValue.FromInteger(descriptor.IssuedAt.Value.ToUnixTimeSeconds()),
                descriptor.Issuer,
                provenance));
        }

        if (descriptor.ExpiresAt is not null)
        {
            claims.Add(new IdentityClaim(
                IdentityClaimTypes.ExpirationTime,
                IdentityClaimValue.FromInteger(descriptor.ExpiresAt.Value.ToUnixTimeSeconds()),
                descriptor.Issuer,
                provenance));
        }

        AddString(IdentityClaimTypes.JwtId, descriptor.JwtId);
        AddString(OpenIdConnectClaimTypes.SessionId, descriptor.SessionId);

        if (descriptor.Events.Count > 0)
        {
            claims.Add(new IdentityClaim(
                OpenIdConnectClaimTypes.Events,
                IdentityClaimValue.FromObject(descriptor.Events),
                descriptor.Issuer,
                provenance));
        }

        foreach (var claim in descriptor.AdditionalClaims)
        {
            ArgumentNullException.ThrowIfNull(claim, nameof(descriptor));

            if (TypedClaimNames.Contains(claim.Type))
            {
                throw new IdentityModelException(
                    $"The extension claim '{claim.Type}' collides with a typed logout token member. " +
                    "Set the typed member instead, so the typed surface and the claim collection cannot disagree.");
            }

            claims.Add(claim);
        }

        return new IdentityClaimCollection(claims);
    }

    // Nonce is deliberately absent: it is not a typed member, and the §2.6 prohibited-
    // nonce negative fixture must stay constructible for Validate() to report it.
    private static readonly HashSet<string> TypedClaimNames = new(StringComparer.Ordinal)
    {
        IdentityClaimTypes.Issuer,
        IdentityClaimTypes.Subject,
        IdentityClaimTypes.Audience,
        IdentityClaimTypes.IssuedAt,
        IdentityClaimTypes.ExpirationTime,
        IdentityClaimTypes.JwtId,
        OpenIdConnectClaimTypes.SessionId,
        OpenIdConnectClaimTypes.Events,
    };
}
