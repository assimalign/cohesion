using System;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Describes the trust and claim rules an <see cref="IJsonWebTokenValidator"/> applies to signed compact
/// JSON Web Tokens: which issuers are trusted with which keys, the lifetime ceiling, the clock skew, and
/// the subject rule.
/// </summary>
/// <remarks>
/// <para>
/// Every profile requires the <c>iss</c>, <c>sub</c>, <c>aud</c>, <c>exp</c>, <c>nbf</c>, <c>iat</c>, and
/// <c>jti</c> claims, a non-blank <c>jti</c>, <c>iat</c> no later than the validation instant plus
/// <see cref="ClockSkew"/>, <c>exp</c> after both <c>iat</c> and <c>nbf</c>, and <c>exp</c> minus
/// <c>iat</c> within <see cref="MaximumLifetime"/>. Audience membership is deliberately not part of the
/// profile: callers distinguish an unauthenticated token from an authenticated token issued for a
/// different audience with <see cref="JsonWebTokenValidator.HasAudience(IJsonWebToken, string?)"/>.
/// </para>
/// <para>
/// The issuer resolver is consulted once per validation with the token's non-blank <c>iss</c>, so a
/// resolver backed by a mutable trust store observes additions and removals immediately.
/// </para>
/// </remarks>
public sealed class JsonWebTokenValidationProfile
{
    private readonly TimeSpan _clockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Initializes a validation profile.</summary>
    /// <param name="issuerKeys">
    /// Resolves an exact <c>iss</c> value to the key set that issuer signs with, or returns
    /// <see langword="null"/> for an untrusted issuer.
    /// </param>
    /// <param name="maximumLifetime">The largest accepted <c>exp</c> minus <c>iat</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="issuerKeys"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumLifetime"/> is not positive.</exception>
    public JsonWebTokenValidationProfile(Func<string, JsonWebKeySet?> issuerKeys, TimeSpan maximumLifetime)
    {
        ArgumentNullException.ThrowIfNull(issuerKeys);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumLifetime, TimeSpan.Zero);

        IssuerKeys = issuerKeys;
        MaximumLifetime = maximumLifetime;
    }

    /// <summary>Initializes a validation profile that trusts one issuer with one key set.</summary>
    /// <param name="issuer">The exact trusted <c>iss</c> value.</param>
    /// <param name="keys">The trusted issuer's key set.</param>
    /// <param name="maximumLifetime">The largest accepted <c>exp</c> minus <c>iat</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="issuer"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumLifetime"/> is not positive.</exception>
    public JsonWebTokenValidationProfile(string issuer, JsonWebKeySet keys, TimeSpan maximumLifetime)
        : this(CreateSingleIssuerResolver(issuer, keys), maximumLifetime)
    {
    }

    /// <summary>Gets the resolver from an exact issuer name to its trusted key set.</summary>
    public Func<string, JsonWebKeySet?> IssuerKeys { get; }

    /// <summary>Gets the largest accepted <c>exp</c> minus <c>iat</c>.</summary>
    public TimeSpan MaximumLifetime { get; }

    /// <summary>Gets the clock skew applied to temporal claims. Defaults to five minutes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan ClockSkew
    {
        get => _clockSkew;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _clockSkew = value;
        }
    }

    /// <summary>
    /// Gets the exact <c>sub</c> value a token must carry, or <see langword="null"/> to accept any subject
    /// that satisfies <see cref="RequireSubject"/>.
    /// </summary>
    public string? ExpectedSubject { get; init; }

    /// <summary>
    /// Gets whether the <c>sub</c> value must be a non-empty string when <see cref="ExpectedSubject"/> is
    /// <see langword="null"/>. Defaults to <see langword="true"/>; a caller that applies its own subject
    /// rule after validation sets it to <see langword="false"/>, leaving only the claim-presence rule.
    /// </summary>
    public bool RequireSubject { get; init; } = true;

    private static Func<string, JsonWebKeySet?> CreateSingleIssuerResolver(string issuer, JsonWebKeySet keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentNullException.ThrowIfNull(keys);

        return candidate => string.Equals(candidate, issuer, StringComparison.Ordinal) ? keys : null;
    }
}
