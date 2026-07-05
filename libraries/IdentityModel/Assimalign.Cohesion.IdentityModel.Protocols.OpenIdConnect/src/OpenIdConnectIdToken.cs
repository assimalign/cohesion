using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents the claims surface of an OpenID Connect ID token — the protocol contract for
/// what an ID token asserts. Compact-serialization parsing and cryptographic validation
/// (signature, <c>at_hash</c>/<c>c_hash</c> computation) belong to the JSON Web Token
/// package, which materializes this contract; <see cref="Validate" /> owns the pure data
/// rules of Core §3.1.3.7.
/// </summary>
/// <remarks>
/// The typed members and the <see cref="Claims" /> collection cannot disagree: the
/// collection is built at materialization from the typed members plus the descriptor's
/// extension claims, each stamped with OpenID Connect provenance, and extension claims
/// that collide with a typed member's claim name are rejected. Numeric-date claims carry
/// their wire shape (integer seconds) in the collection while the typed members expose
/// <see cref="DateTimeOffset" /> convenience values. Claim-presence REQUIREDness (the
/// Core §2 five) is deliberately a <see cref="Validate" /> diagnostic, not a guard, so
/// negative compliance fixtures stay constructible.
/// </remarks>
public sealed class OpenIdConnectIdToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectIdToken" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The token contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a list entry is null or whitespace.</exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when an extension claim's type collides with a typed member's claim name,
    /// or a claims source entry is null.
    /// </exception>
    public OpenIdConnectIdToken(OpenIdConnectIdTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Issuer = descriptor.Issuer;
        Subject = descriptor.Subject;
        Audiences = ModelSnapshot.Strings(descriptor.Audiences, nameof(descriptor));
        ExpiresAt = descriptor.ExpiresAt;
        IssuedAt = descriptor.IssuedAt;
        NotBefore = descriptor.NotBefore;
        AuthTime = descriptor.AuthTime;
        Nonce = descriptor.Nonce;
        Acr = descriptor.Acr;
        Amr = ModelSnapshot.Strings(descriptor.Amr, nameof(descriptor));
        Azp = descriptor.Azp;
        AccessTokenHash = descriptor.AccessTokenHash;
        CodeHash = descriptor.CodeHash;
        SessionId = descriptor.SessionId;
        JwtId = descriptor.JwtId;
        RawToken = descriptor.RawToken;
        ClaimsSources = SnapshotSources(descriptor.ClaimsSources);
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
    /// Gets the expiration instant (<c>exp</c>).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Gets the issuance instant (<c>iat</c>).
    /// </summary>
    public DateTimeOffset? IssuedAt { get; }

    /// <summary>
    /// Gets the not-before instant (<c>nbf</c>).
    /// </summary>
    public DateTimeOffset? NotBefore { get; }

    /// <summary>
    /// Gets the authentication instant (<c>auth_time</c>).
    /// </summary>
    public DateTimeOffset? AuthTime { get; }

    /// <summary>
    /// Gets the replay-prevention nonce (<c>nonce</c>).
    /// </summary>
    public string? Nonce { get; }

    /// <summary>
    /// Gets the authentication context class reference (<c>acr</c>).
    /// </summary>
    public string? Acr { get; }

    /// <summary>
    /// Gets the authentication method references (<c>amr</c>).
    /// </summary>
    public IReadOnlyList<string> Amr { get; }

    /// <summary>
    /// Gets the authorized party (<c>azp</c>).
    /// </summary>
    public string? Azp { get; }

    /// <summary>
    /// Gets the access token hash (<c>at_hash</c>). Hash <em>computation</em> is the JSON
    /// Web Token package's concern; the member preserves the wire value.
    /// </summary>
    public string? AccessTokenHash { get; }

    /// <summary>
    /// Gets the code hash (<c>c_hash</c>).
    /// </summary>
    public string? CodeHash { get; }

    /// <summary>
    /// Gets the provider session identifier (<c>sid</c>).
    /// </summary>
    public string? SessionId { get; }

    /// <summary>
    /// Gets the token identifier (<c>jti</c>).
    /// </summary>
    public string? JwtId { get; }

    /// <summary>
    /// Gets the original compact serialization, when retained.
    /// </summary>
    public string? RawToken { get; }

    /// <summary>
    /// Gets the unresolved aggregated and distributed claims references. They never
    /// appear in <see cref="Claims" />.
    /// </summary>
    public IReadOnlyList<OpenIdConnectClaimsSource> ClaimsSources { get; }

    /// <summary>
    /// Gets every claim the token asserts — the typed members and the extension claims —
    /// with OpenID Connect provenance. This is the surface cross-protocol normalization
    /// maps from.
    /// </summary>
    public IIdentityClaimCollection Claims { get; }

    /// <summary>
    /// Validates the token's data rules per OpenID Connect Core §3.1.3.7: the required
    /// claim set, issuer match, audience and authorized-party rules, temporal windows,
    /// nonce match, and authentication-age limits. Signature and hash verification are
    /// the JSON Web Token package's concern and are deliberately absent here.
    /// </summary>
    /// <param name="options">The validation expectations.</param>
    /// <returns>The validation findings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is null.</exception>
    public ProtocolValidationResult Validate(OpenIdConnectIdTokenValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<ProtocolValidationDiagnostic>();

        ValidateRequiredClaims(diagnostics);
        ValidateIssuer(diagnostics, options);
        ValidateAudiences(diagnostics, options);
        ValidateLifetime(diagnostics, options);
        ValidateNonce(diagnostics, options);
        ValidateAuthenticationAge(diagnostics, options);

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }

    private void ValidateRequiredClaims(List<ProtocolValidationDiagnostic> diagnostics)
    {
        if (Issuer is null)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.Issuer));
        }

        if (Subject is null)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.Subject));
        }

        if (Audiences.Count == 0)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.Audience));
        }

        if (ExpiresAt is null)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.ExpirationTime));
        }

        if (IssuedAt is null)
        {
            diagnostics.Add(MissingClaim(IdentityClaimTypes.IssuedAt));
        }
    }

    private void ValidateIssuer(List<ProtocolValidationDiagnostic> diagnostics, OpenIdConnectIdTokenValidationOptions options)
    {
        if (options.ExpectedIssuer is not null && Issuer is not null &&
            !string.Equals(Issuer, options.ExpectedIssuer, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.IssuerMismatch,
                "The ID token issuer does not match the expected issuer.",
                member: IdentityClaimTypes.Issuer));
        }
    }

    private void ValidateAudiences(List<ProtocolValidationDiagnostic> diagnostics, OpenIdConnectIdTokenValidationOptions options)
    {
        if (options.ExpectedAudience is null || Audiences.Count == 0)
        {
            return;
        }

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
                "The ID token audiences do not include the client.",
                member: IdentityClaimTypes.Audience));
        }

        // Core §3.1.3.7 step 3: additional audiences are untrusted BY DEFAULT — the
        // spec's posture, not "no opinion". Callers extend the trusted set through the
        // options, or opt out explicitly.
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
                        $"The ID token lists an untrusted additional audience '{audience}'.",
                        member: IdentityClaimTypes.Audience));
                }
            }
        }

        // Step 4 (azp SHOULD be present) applies to multi-audience tokens; step 5 (azp
        // SHOULD equal the client) applies whenever azp is present. Both are SHOULDs:
        // warn, never fail.
        if (Audiences.Count > 1 && Azp is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Warning,
                OpenIdConnectValidationCodes.AzpInvalid,
                "A multi-audience ID token should carry an authorized party (azp) claim.",
                member: OpenIdConnectClaimTypes.Azp));
        }

        if (Azp is not null && !string.Equals(Azp, options.ExpectedAudience, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Warning,
                OpenIdConnectValidationCodes.AzpInvalid,
                "The authorized party (azp) should be the client.",
                member: OpenIdConnectClaimTypes.Azp));
        }
    }

    private void ValidateLifetime(List<ProtocolValidationDiagnostic> diagnostics, OpenIdConnectIdTokenValidationOptions options)
    {
        // The skew is always applied to the caller-controlled instant, never the wire
        // timestamp: extreme wire values (DateTimeOffset.MaxValue-adjacent) would make
        // timestamp-plus-skew arithmetic throw instead of diagnose.
        if (ExpiresAt is not null && options.ValidateAt - options.ClockSkew >= ExpiresAt.Value)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.Expired,
                "The ID token is expired.",
                member: IdentityClaimTypes.ExpirationTime));
        }

        if (IssuedAt is not null && IssuedAt.Value > options.ValidateAt + options.ClockSkew)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.NotYetValid,
                "The ID token is issued in the future.",
                member: IdentityClaimTypes.IssuedAt));
        }

        if (NotBefore is not null && options.ValidateAt + options.ClockSkew < NotBefore.Value)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.NotYetValid,
                "The ID token is not yet valid.",
                member: IdentityClaimTypes.NotBefore));
        }
    }

    private void ValidateNonce(List<ProtocolValidationDiagnostic> diagnostics, OpenIdConnectIdTokenValidationOptions options)
    {
        if (options.ExpectedNonce is null)
        {
            return;
        }

        if (Nonce is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.NonceMissing,
                "A nonce was sent with the request but the ID token carries none.",
                member: OpenIdConnectClaimTypes.Nonce));
        }
        else if (!string.Equals(Nonce, options.ExpectedNonce, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.NonceMismatch,
                "The ID token nonce does not match the request's nonce.",
                member: OpenIdConnectClaimTypes.Nonce));
        }
    }

    private void ValidateAuthenticationAge(List<ProtocolValidationDiagnostic> diagnostics, OpenIdConnectIdTokenValidationOptions options)
    {
        var authTimeRequired = options.MaxAge is not null || options.RequireAuthTime;

        if (authTimeRequired && AuthTime is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.AuthTimeMissing,
                "The auth_time claim is required but absent.",
                member: OpenIdConnectClaimTypes.AuthTime));
            return;
        }

        // Compare ages in seconds through double arithmetic: TimeSpan.FromSeconds on an
        // extreme wire max_age would throw instead of diagnose.
        if (options.MaxAge is not null && AuthTime is not null &&
            (options.ValidateAt - AuthTime.Value - options.ClockSkew).TotalSeconds > options.MaxAge.Value)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                OpenIdConnectValidationCodes.MaxAgeExceeded,
                "The authentication is older than the requested maximum age.",
                member: OpenIdConnectClaimTypes.AuthTime));
        }
    }

    private static ProtocolValidationDiagnostic MissingClaim(string claim)
        => new(
            ProtocolValidationSeverity.Error,
            ProtocolValidationCodes.MissingRequiredMember,
            $"The ID token is missing the required '{claim}' claim.",
            member: claim);

    private static IReadOnlyList<OpenIdConnectClaimsSource> SnapshotSources(IList<OpenIdConnectClaimsSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
        {
            return Array.Empty<OpenIdConnectClaimsSource>();
        }

        var snapshot = new OpenIdConnectClaimsSource[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            snapshot[index] = sources[index]
                ?? throw new IdentityModelException("Claims source lists must not contain null entries.");
        }

        return new ReadOnlyCollection<OpenIdConnectClaimsSource>(snapshot);
    }

    private static IIdentityClaimCollection BuildClaims(OpenIdConnectIdTokenDescriptor descriptor)
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

        void AddInstant(string type, DateTimeOffset? value)
        {
            // NumericDate claims keep their wire shape: integer seconds since the epoch.
            if (value is not null)
            {
                claims.Add(new IdentityClaim(
                    type,
                    IdentityClaimValue.FromInteger(value.Value.ToUnixTimeSeconds()),
                    descriptor.Issuer,
                    provenance));
            }
        }

        AddString(IdentityClaimTypes.Issuer, descriptor.Issuer);
        AddString(IdentityClaimTypes.Subject, descriptor.Subject);

        foreach (var audience in descriptor.Audiences)
        {
            AddString(IdentityClaimTypes.Audience, audience);
        }

        AddInstant(IdentityClaimTypes.ExpirationTime, descriptor.ExpiresAt);
        AddInstant(IdentityClaimTypes.IssuedAt, descriptor.IssuedAt);
        AddInstant(IdentityClaimTypes.NotBefore, descriptor.NotBefore);
        AddInstant(OpenIdConnectClaimTypes.AuthTime, descriptor.AuthTime);
        AddString(OpenIdConnectClaimTypes.Nonce, descriptor.Nonce);
        AddString(OpenIdConnectClaimTypes.Acr, descriptor.Acr);

        foreach (var method in descriptor.Amr)
        {
            AddString(OpenIdConnectClaimTypes.Amr, method);
        }

        AddString(OpenIdConnectClaimTypes.Azp, descriptor.Azp);
        AddString(OpenIdConnectClaimTypes.AccessTokenHash, descriptor.AccessTokenHash);
        AddString(OpenIdConnectClaimTypes.CodeHash, descriptor.CodeHash);
        AddString(OpenIdConnectClaimTypes.SessionId, descriptor.SessionId);
        AddString(IdentityClaimTypes.JwtId, descriptor.JwtId);

        foreach (var claim in descriptor.AdditionalClaims)
        {
            ArgumentNullException.ThrowIfNull(claim, nameof(descriptor));

            if (TypedClaimNames.Contains(claim.Type))
            {
                throw new IdentityModelException(
                    $"The extension claim '{claim.Type}' collides with a typed ID token member. " +
                    "Set the typed member instead, so the typed surface and the claim collection cannot disagree.");
            }

            claims.Add(claim);
        }

        return new IdentityClaimCollection(claims);
    }

    private static readonly HashSet<string> TypedClaimNames = new(StringComparer.Ordinal)
    {
        IdentityClaimTypes.Issuer,
        IdentityClaimTypes.Subject,
        IdentityClaimTypes.Audience,
        IdentityClaimTypes.ExpirationTime,
        IdentityClaimTypes.IssuedAt,
        IdentityClaimTypes.NotBefore,
        IdentityClaimTypes.JwtId,
        OpenIdConnectClaimTypes.AuthTime,
        OpenIdConnectClaimTypes.Nonce,
        OpenIdConnectClaimTypes.Acr,
        OpenIdConnectClaimTypes.Amr,
        OpenIdConnectClaimTypes.Azp,
        OpenIdConnectClaimTypes.AccessTokenHash,
        OpenIdConnectClaimTypes.CodeHash,
        OpenIdConnectClaimTypes.SessionId,
    };
}
