using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// The JWT bearer authentication handler: parses the <c>Authorization: Bearer</c> credential,
/// verifies the token signature through the configured keys, validates the document rules through
/// the IdentityModel JSON Web Token contracts, maps the token onto a principal, and emits RFC 6750
/// <c>WWW-Authenticate</c> challenges.
/// </summary>
internal sealed class JwtBearerHandler : IAuthenticationHandler
{
    private readonly JwtBearerOptions _options;

    private AuthenticationScheme _scheme = null!;
    private IHttpContext _context = null!;
    private AuthenticateResult? _cachedResult;

    public JwtBearerHandler(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
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
            return AuthenticateResult.Fail("The Bearer credential carries no token.");
        }

        if (!JsonWebToken.TryParse(token, out JsonWebToken? jwt) || jwt is null)
        {
            return AuthenticateResult.Fail("The bearer token is malformed.");
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
            return AuthenticateResult.Fail(DescribeFailure(validation));
        }

        if (!IssuerIsValid(jwt))
        {
            return AuthenticateResult.Fail("The bearer token issuer is not accepted.");
        }

        if (!AudienceIsValid(jwt))
        {
            return AuthenticateResult.Fail("The bearer token audience is not accepted.");
        }

        var principal = JwtClaimsPrincipalMapper.Map(jwt, _scheme.Name, _options);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, new AuthenticationProperties(), _scheme.Name));
    }

    /// <inheritdoc />
    public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        _context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = BuildChallenge();
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

        _context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = challenge;
        _context.Response.StatusCode = HttpStatusCode.Forbidden;
        return Task.CompletedTask;
    }

    private AuthenticateResult? VerifySignature(JsonWebToken jwt)
    {
        JsonWebTokenParts? parts = jwt.Parts;
        string? algorithm = jwt.Algorithm;

        if (parts is null || jwt.SigningInput is null || algorithm is null)
        {
            return AuthenticateResult.Fail("The bearer token cannot be verified: it is not in compact JWS form.");
        }

        if (string.Equals(algorithm, JoseAlgorithms.None, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("The bearer token uses the unsecured 'none' algorithm.");
        }

        byte[] signature;
        try
        {
            signature = Base64Url.DecodeFromChars(parts.Signature);
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("The bearer token signature is not valid base64url.");
        }

        byte[] signingInput = Encoding.ASCII.GetBytes(jwt.SigningInput);
        string? keyId = jwt.Header.KeyId;

        foreach (IJwtSignatureVerifier verifier in _options.SigningKeys)
        {
            if (verifier.CanVerify(algorithm, keyId) && verifier.Verify(algorithm, signingInput, signature))
            {
                return null;
            }
        }

        return AuthenticateResult.Fail("The bearer token signature could not be verified.");
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

    private bool IssuerIsValid(JsonWebToken jwt)
    {
        if (_options.ValidIssuers.Count == 0)
        {
            return true;
        }

        return jwt.Issuer is not null && ContainsOrdinal(_options.ValidIssuers, jwt.Issuer);
    }

    private bool AudienceIsValid(JsonWebToken jwt)
    {
        if (_options.ValidAudiences.Count == 0)
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
