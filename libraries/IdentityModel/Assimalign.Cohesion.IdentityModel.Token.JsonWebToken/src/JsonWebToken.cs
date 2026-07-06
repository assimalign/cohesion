using System;
using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Represents an immutable JSON Web Token materialized onto the canonical identity model. The
/// registered and OpenID Connect ID-token accessors are computed from the authoritative
/// <see cref="IIdentityToken.Claims" /> record, so they can never disagree with it.
/// </summary>
/// <remarks>
/// <para>
/// This is the concrete JOSE/JWT document layer. It parses compact serialization, models the
/// typed JOSE header, and validates the document-level rules (algorithm, required claims, and
/// the keyless <c>at_hash</c>/<c>c_hash</c> value comparison). It does <em>not</em> verify the
/// signature — that keyed operation is exposed as a seam through
/// <see cref="IJsonWebToken.SigningInput" /> and <see cref="Parts" /> and belongs to a
/// Security-layer package. The OpenID Connect <em>protocol</em> data rules (nonce match,
/// authorized-party posture, <c>max_age</c>) belong to the OpenID Connect branch.
/// </para>
/// </remarks>
public sealed class JsonWebToken : IdentityToken, IJsonWebToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JsonWebToken" /> class by snapshotting the
    /// provided descriptor.
    /// </summary>
    /// <param name="descriptor">The JWT descriptor.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    public JsonWebToken(JsonWebTokenDescriptor descriptor)
        : base(IdentityTokenKind.JsonWebToken, Prepare(descriptor))
    {
        Header = new JoseHeader(descriptor.Header);
        Parts = descriptor.Parts;
    }

    /// <inheritdoc />
    public JoseHeader Header { get; }

    /// <inheritdoc />
    public string? Algorithm => Header.Algorithm;

    /// <inheritdoc />
    public JsonWebTokenParts? Parts { get; }

    /// <inheritdoc />
    public string? SigningInput => Parts?.SigningInput;

    /// <summary>
    /// Gets the replay-prevention nonce (<c>nonce</c>).
    /// </summary>
    public string? Nonce => Claims.GetString(JsonWebTokenClaimTypes.Nonce);

    /// <summary>
    /// Gets the authorized party (<c>azp</c>).
    /// </summary>
    public string? AuthorizedParty => Claims.GetString(JsonWebTokenClaimTypes.AuthorizedParty);

    /// <summary>
    /// Gets the authentication instant (<c>auth_time</c>), or <see langword="null" /> when
    /// absent or out of the representable range.
    /// </summary>
    public DateTimeOffset? AuthTime =>
        Claims.TryGet(JsonWebTokenClaimTypes.AuthTime, out var claim)
            ? JwtNumericDate.FromClaimValue(claim.Value)
            : null;

    /// <summary>
    /// Gets the access token hash (<c>at_hash</c>), as the wire value.
    /// </summary>
    public string? AccessTokenHash => Claims.GetString(JsonWebTokenClaimTypes.AccessTokenHash);

    /// <summary>
    /// Gets the authorization code hash (<c>c_hash</c>), as the wire value.
    /// </summary>
    public string? CodeHash => Claims.GetString(JsonWebTokenClaimTypes.CodeHash);

    /// <summary>
    /// Gets the provider session identifier (<c>sid</c>).
    /// </summary>
    public string? SessionId => Claims.GetString(JsonWebTokenClaimTypes.SessionId);

    /// <summary>
    /// Gets the authentication context class reference (<c>acr</c>).
    /// </summary>
    public string? AuthenticationContextClassReference =>
        Claims.GetString(JsonWebTokenClaimTypes.AuthenticationContextClassReference);

    /// <summary>
    /// Gets the authentication method references (<c>amr</c>).
    /// </summary>
    public IReadOnlyList<string> AuthenticationMethodReferences
    {
        get
        {
            var values = Claims.GetValues(JsonWebTokenClaimTypes.AuthenticationMethodReferences);
            if (values.Count == 0)
            {
                return Array.Empty<string>();
            }

            var methods = new List<string>(values.Count);
            foreach (var value in values)
            {
                if (value.TryGetString(out var method))
                {
                    methods.Add(method);
                }
            }

            return methods;
        }
    }

    /// <summary>
    /// Parses a compact JWS-serialized JSON Web Token. Signatures are not verified.
    /// </summary>
    /// <param name="token">The compact token value.</param>
    /// <returns>The parsed token.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="token" /> is null or whitespace.</exception>
    /// <exception cref="FormatException">
    /// Thrown when the compact serialization, base64url encoding, or JSON is malformed, or a
    /// JSON object contains a duplicate member.
    /// </exception>
    public static JsonWebToken Parse(string token) => new(JsonWebTokenParser.Parse(token));

    /// <summary>
    /// Attempts to parse a compact JWS-serialized JSON Web Token. Signatures are not verified.
    /// </summary>
    /// <param name="token">The compact token value.</param>
    /// <param name="jsonWebToken">When this method returns, contains the parsed token, if parsing succeeded.</param>
    /// <returns><see langword="true" /> when parsing succeeded; otherwise <see langword="false" />.</returns>
    public static bool TryParse(string token, out JsonWebToken? jsonWebToken)
    {
        try
        {
            jsonWebToken = Parse(token);
            return true;
        }
        catch (FormatException)
        {
            jsonWebToken = null;
            return false;
        }
        catch (ArgumentException)
        {
            jsonWebToken = null;
            return false;
        }
        catch (IdentityModelException)
        {
            // A structurally valid but semantically rejected value (an over-deep claim graph,
            // an undefined value) surfaces as a parse failure rather than an escaping exception.
            jsonWebToken = null;
            return false;
        }
    }

    /// <summary>
    /// Validates the token's document-level rules: the neutral issuer/audience/temporal checks,
    /// the algorithm (present, not <c>none</c> unless allowed, within the allowed set), any
    /// unencoded-payload or unrecognized-critical-header rejection, required-claim presence, and
    /// the keyless <c>at_hash</c>/<c>c_hash</c> value comparison when the caller supplies the
    /// access token or authorization code. It does not verify the signature or enforce OpenID
    /// Connect protocol rules.
    /// </summary>
    /// <param name="options">The validation expectations.</param>
    /// <returns>The validation findings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is null.</exception>
    public TokenValidationResult Validate(JsonWebTokenValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<TokenValidationDiagnostic>();

        // Compose the neutral base rules (issuer / audience / temporal) rather than
        // re-implementing them.
        var baseResult = base.Validate(new IdentityTokenValidationOptions(options.ValidateAt)
        {
            ClockSkew = options.ClockSkew,
            ExpectedIssuer = options.ExpectedIssuer,
            ExpectedAudience = options.ExpectedAudience,
        });
        diagnostics.AddRange(baseResult.Diagnostics);

        ValidateAlgorithm(diagnostics, options);
        ValidateHeaderConstraints(diagnostics, options);
        ValidateRequiredClaims(diagnostics, options);
        ValidateTokenHash(diagnostics, options.AccessToken, AccessTokenHash, options.RequireTokenHash,
            JsonWebTokenClaimTypes.AccessTokenHash, JsonWebTokenValidationCodes.AccessTokenHashMismatch);
        ValidateTokenHash(diagnostics, options.AuthorizationCode, CodeHash, options.RequireTokenHash,
            JsonWebTokenClaimTypes.CodeHash, JsonWebTokenValidationCodes.CodeHashMismatch);

        return diagnostics.Count == 0 ? TokenValidationResult.Success : new TokenValidationResult(diagnostics);
    }

    private void ValidateAlgorithm(List<TokenValidationDiagnostic> diagnostics, JsonWebTokenValidationOptions options)
    {
        var algorithm = Header.Algorithm;

        if (algorithm is null)
        {
            diagnostics.Add(Error(
                TokenValidationCodes.MissingRequiredMember,
                "The JOSE header is missing the required 'alg' parameter.",
                JoseHeaderParameterNames.Algorithm));
            return;
        }

        if (Header.IsUnsecured && !options.AllowUnsecured)
        {
            diagnostics.Add(Error(
                JsonWebTokenValidationCodes.AlgorithmNone,
                "The token uses the unsecured 'none' algorithm, which is rejected by default (RFC 8725).",
                JoseHeaderParameterNames.Algorithm));
        }

        if (options.AllowedAlgorithms.Count > 0 && !ContainsOrdinal(options.AllowedAlgorithms, algorithm))
        {
            diagnostics.Add(Error(
                JsonWebTokenValidationCodes.UnsupportedAlgorithm,
                $"The token algorithm '{algorithm}' is not in the allowed set.",
                JoseHeaderParameterNames.Algorithm));
        }
    }

    private void ValidateHeaderConstraints(List<TokenValidationDiagnostic> diagnostics, JsonWebTokenValidationOptions options)
    {
        if (!Header.RequiresBase64Payload)
        {
            diagnostics.Add(Error(
                JsonWebTokenValidationCodes.UnsupportedBase64Payload,
                "The token selects the unencoded-payload (b64:false) variant, which is not supported.",
                JoseHeaderParameterNames.Base64Payload));
        }

        foreach (var critical in Header.Critical)
        {
            if (!ContainsOrdinal(options.KnownCriticalHeaders, critical))
            {
                diagnostics.Add(Error(
                    JsonWebTokenValidationCodes.UnrecognizedCriticalHeader,
                    $"The header marks '{critical}' critical but the caller does not declare it understood.",
                    JoseHeaderParameterNames.Critical));
            }
        }
    }

    private void ValidateRequiredClaims(List<TokenValidationDiagnostic> diagnostics, JsonWebTokenValidationOptions options)
    {
        foreach (var claim in options.RequiredClaims)
        {
            if (!Claims.Contains(claim))
            {
                diagnostics.Add(Error(
                    TokenValidationCodes.MissingRequiredMember,
                    $"The token is missing the required '{claim}' claim.",
                    claim));
            }
        }
    }

    private void ValidateTokenHash(
        List<TokenValidationDiagnostic> diagnostics,
        string? value,
        string? claimHash,
        bool require,
        string member,
        string mismatchCode)
    {
        if (value is null)
        {
            return;
        }

        if (claimHash is null)
        {
            diagnostics.Add(new TokenValidationDiagnostic(
                require ? TokenValidationSeverity.Error : TokenValidationSeverity.Warning,
                JsonWebTokenValidationCodes.TokenHashMissing,
                $"The '{member}' claim is absent although the value to hash was supplied.",
                member));
            return;
        }

        var algorithm = Header.Algorithm;
        if (algorithm is null || !TokenHashComputer.HasDefinedHash(algorithm))
        {
            diagnostics.Add(Error(
                JsonWebTokenValidationCodes.UnsupportedAlgorithm,
                $"The '{member}' claim cannot be verified because the algorithm has no defined hash.",
                member));
            return;
        }

        var expected = TokenHashComputer.ComputeHalfHash(algorithm, value);
        if (!string.Equals(expected, claimHash, StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                mismatchCode,
                $"The '{member}' claim does not match the hash of the supplied value.",
                member));
        }
    }

    private static TokenValidationDiagnostic Error(string code, string message, string? member = null)
        => new(TokenValidationSeverity.Error, code, message, member);

    private static bool ContainsOrdinal(IList<string> values, string candidate)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonWebTokenDescriptor Prepare(JsonWebTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor;
    }
}
