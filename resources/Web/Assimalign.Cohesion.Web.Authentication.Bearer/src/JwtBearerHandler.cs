using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using Assimalign.Cohesion.Web.Authentication.Bearer.Internal;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>
/// The JWT bearer authentication handler: parses the <c>Authorization: Bearer</c> credential,
/// verifies the token signature through the configured keys, validates the document rules through
/// the IdentityModel JSON Web Token contracts, maps the token onto a principal, and emits RFC 6750
/// <c>WWW-Authenticate</c> challenges.
/// </summary>
public sealed class JwtBearerHandler : IAuthenticationHandler
{
    private readonly JwtBearerOptions _options;

    private AuthenticationScheme _scheme = null!;
    private IHttpContext _context = null!;
    private AuthenticateResult? _cachedResult;

    private JwtBearerHandler(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Creates a JWT bearer authentication handler over the supplied options.
    /// </summary>
    /// <param name="options">The configured bearer options.</param>
    /// <returns>A JWT bearer authentication handler.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="JwtBearerOptions.RequireSignedTokens"/> is <see langword="true"/> but no signing keys
    /// are configured; or <see cref="JwtBearerOptions.ValidateIssuer"/> is <see langword="true"/> but
    /// <see cref="JwtBearerOptions.ValidIssuers"/> is empty; or
    /// <see cref="JwtBearerOptions.ValidateAudience"/> is <see langword="true"/> but
    /// <see cref="JwtBearerOptions.ValidAudiences"/> is empty.
    /// </exception>
    public static JwtBearerHandler Create(JwtBearerOptions options)
    {
        Validate(options);
        return new JwtBearerHandler(options);
    }

    /// <summary>
    /// Validates bearer options against the fail-closed defaults: a signed-token scheme needs a
    /// signing key, and issuer and audience validation need at least one accepted value each
    /// unless the application opted out explicitly.
    /// </summary>
    /// <param name="options">The configured bearer options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A required value is missing; the message names the property and the opt-out.</exception>
    internal static void Validate(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RequireSignedTokens && options.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "JwtBearerOptions requires at least one signing key when RequireSignedTokens is true.");
        }

        if (options.ValidateIssuer && options.ValidIssuers.Count == 0)
        {
            throw new InvalidOperationException(
                "JwtBearerOptions requires at least one entry in ValidIssuers while ValidateIssuer is true. " +
                "Add the issuer (the token's 'iss') the scheme accepts, or set ValidateIssuer = false to accept any issuer a signing key verifies.");
        }

        if (options.ValidateAudience && options.ValidAudiences.Count == 0)
        {
            throw new InvalidOperationException(
                "JwtBearerOptions requires at least one entry in ValidAudiences while ValidateAudience is true. " +
                "Add the audience (the token's 'aud') this service answers to, or set ValidateAudience = false to accept tokens minted for any audience.");
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(context);

        _scheme = scheme;
        _context = context;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _cachedResult ??= AuthenticateCore();
        return Task.FromResult(_cachedResult);
    }

    private AuthenticateResult AuthenticateCore()
    {
        string? header = _context.Request.Headers.GetValue(HttpHeaderKey.Authorization);
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!TryReadBearerToken(header, out string? token))
        {
            // A non-Bearer Authorization header is simply not this scheme's credential.
            return AuthenticateResult.NoResult();
        }

        if (string.IsNullOrEmpty(token))
        {
            return Reject("EmptyCredential", "The Bearer credential carries no token.");
        }

        if (!JsonWebToken.TryParse(token, out JsonWebToken? jwt) || jwt is null)
        {
            return Reject("Malformed", "The bearer token is malformed.");
        }

        if (_options.RequireSignedTokens)
        {
            AuthenticateResult? signatureFailure = VerifySignature(jwt);
            if (signatureFailure is not null)
            {
                return signatureFailure;
            }
        }

        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        TokenValidationResult validation = jwt.Validate(BuildValidationOptions(now));
        if (!validation.Succeeded)
        {
            return Reject("ValidationFailed", DescribeFailure(validation), jwt, validation);
        }

        if (!IssuerIsValid(jwt))
        {
            return Reject("IssuerRejected", "The bearer token issuer is not accepted.", jwt);
        }

        if (!AudienceIsValid(jwt))
        {
            return Reject("AudienceRejected", "The bearer token audience is not accepted.", jwt);
        }

        var principal = JwtClaimsPrincipalMapper.Map(jwt, _scheme.Name, _options);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, new AuthenticationProperties(), _scheme.Name));
    }

    /// <inheritdoc />
    public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        _context.Response.Headers.AppendValue(HttpHeaderKey.WWWAuthenticate, BuildChallenge());
        _context.Response.StatusCode = HttpStatusCode.Unauthorized;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        // RFC 6750 §3.1: an authenticated-but-insufficient request answers 403 with an
        // insufficient_scope challenge.
        string challenge = _options.Realm is { Length: > 0 } realm
            ? $"Bearer realm=\"{Sanitize(realm)}\", error=\"insufficient_scope\""
            : "Bearer error=\"insufficient_scope\"";

        _context.Response.Headers.AppendValue(HttpHeaderKey.WWWAuthenticate, challenge);
        _context.Response.StatusCode = HttpStatusCode.Forbidden;
        return Task.CompletedTask;
    }

    private AuthenticateResult? VerifySignature(JsonWebToken jwt)
    {
        JsonWebTokenParts? parts = jwt.Parts;
        string? algorithm = jwt.Algorithm;

        if (parts is null || jwt.SigningInput is null || algorithm is null)
        {
            return Reject("NotCompactJws", "The bearer token cannot be verified: it is not in compact JWS form.", jwt);
        }

        if (string.Equals(algorithm, JoseAlgorithms.None, StringComparison.Ordinal))
        {
            return Reject("UnsecuredAlgorithm", "The bearer token uses the unsecured 'none' algorithm.", jwt);
        }

        byte[] signature;
        try
        {
            signature = Base64Url.DecodeFromChars(parts.Signature);
        }
        catch (FormatException)
        {
            return Reject("SignatureEncoding", "The bearer token signature is not valid base64url.", jwt);
        }

        byte[] signingInput = Encoding.ASCII.GetBytes(jwt.SigningInput);
        string? keyId = jwt.Header.KeyId;

        bool matchingKey = false;
        foreach (IJwtSignatureVerifier verifier in _options.SigningKeys)
        {
            if (!verifier.CanVerify(algorithm, keyId))
            {
                continue;
            }

            matchingKey = true;
            if (verifier.Verify(algorithm, signingInput, signature))
            {
                return null;
            }
        }

        return Reject(matchingKey ? "SignatureInvalid" : "NoMatchingKey",
            "The bearer token signature could not be verified.", jwt);
    }

    private AuthenticateResult Reject(string reason, string message, JsonWebToken? jwt = null, TokenValidationResult? validation = null)
    {
        JwtBearerEventSource.Log.TokenRejected(_context, _scheme.Name, reason, jwt, validation);
        return AuthenticateResult.Fail(message);
    }

    private JsonWebTokenValidationOptions BuildValidationOptions(DateTimeOffset now)
    {
        JsonWebTokenValidationOptions options = new(now)
        {
            ClockSkew = _options.ClockSkew,
            AllowUnsecured = false,
        };

        foreach (string algorithm in _options.AllowedAlgorithms)
        {
            options.AllowedAlgorithms.Add(algorithm);
        }

        // Issuer/audience are validated here for any-of semantics, so they are left unset on the
        // single-value document options.
        return options;
    }

    // Fail closed: validation is on unless the application turned it off, and an empty accepted
    // list with validation on matches nothing (JwtBearerHandler.Validate rejects that
    // configuration up front, so this is the defense for options mutated after registration).
    private bool IssuerIsValid(JsonWebToken jwt)
    {
        if (!_options.ValidateIssuer)
        {
            return true;
        }

        return jwt.Issuer is not null && ContainsOrdinal(_options.ValidIssuers, jwt.Issuer);
    }

    private bool AudienceIsValid(JsonWebToken jwt)
    {
        if (!_options.ValidateAudience)
        {
            return true;
        }

        foreach (string audience in jwt.Audiences)
        {
            if (ContainsOrdinal(_options.ValidAudiences, audience))
            {
                return true;
            }
        }

        return false;
    }

    private string BuildChallenge()
    {
        var parameters = new List<string>(3);

        if (_options.Realm is { Length: > 0 } realm)
        {
            parameters.Add($"realm=\"{Sanitize(realm)}\"");
        }

        // Surface the last authenticate failure per RFC 6750 §3 so a client learns why.
        if (_cachedResult is { Succeeded: false, None: false, Failure: { } failure })
        {
            parameters.Add("error=\"invalid_token\"");
            parameters.Add($"error_description=\"{Sanitize(failure.Message)}\"");
        }

        return parameters.Count == 0 ? "Bearer" : "Bearer " + string.Join(", ", parameters);
    }

    private static bool TryReadBearerToken(string header, out string? token)
    {
        token = null;

        const string prefix = JwtBearerDefaults.BearerPrefix;
        if (header.Length <= prefix.Length ||
            !header.AsSpan(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            !char.IsWhiteSpace(header[prefix.Length]))
        {
            return false;
        }

        token = header[(prefix.Length + 1)..].Trim();
        return true;
    }

    private static string DescribeFailure(TokenValidationResult validation)
        => validation.Errors.Count > 0 ? validation.Errors[0].Message : "The bearer token failed validation.";

    private static bool ContainsOrdinal(IList<string> values, string candidate)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Strip characters that would break the quoted-string in a WWW-Authenticate header value.
    private static string Sanitize(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int length = 0;

        foreach (char c in value)
        {
            if (c is not '"' and not '\\' and not '\r' and not '\n')
            {
                buffer[length++] = c;
            }
        }

        return new string(buffer[..length]);
    }
}
