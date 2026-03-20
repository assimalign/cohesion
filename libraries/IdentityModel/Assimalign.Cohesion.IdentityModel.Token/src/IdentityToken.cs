using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Provides a shared immutable implementation for identity token formats.
/// </summary>
public abstract class IdentityToken : IIdentityToken
{
    private readonly string[] _audiences;
    private readonly IIdentityTokenClaim[] _claims;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityToken" /> class.
    /// </summary>
    /// <param name="kind">The token wire format.</param>
    /// <param name="descriptor">The normalized token contents.</param>
    protected IdentityToken(IdentityTokenKind kind, IdentityTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Kind = kind;
        Id = descriptor.Id;
        Subject = descriptor.Subject;
        Issuer = descriptor.Issuer;
        TokenType = descriptor.TokenType;
        RawData = descriptor.RawData;
        IssuedAt = descriptor.IssuedAt;
        NotBefore = descriptor.NotBefore;
        ExpiresAt = descriptor.ExpiresAt;

        _audiences = CopyAudiences(descriptor.Audiences);
        _claims = CopyClaims(descriptor.Claims);
        Properties = new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(descriptor.Properties, StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public IdentityTokenKind Kind { get; }

    /// <inheritdoc />
    public string? Id { get; }

    /// <inheritdoc />
    public string? Subject { get; }

    /// <inheritdoc />
    public string? Issuer { get; }

    /// <inheritdoc />
    public string? TokenType { get; }

    /// <inheritdoc />
    public string? RawData { get; }

    /// <inheritdoc />
    public DateTimeOffset? IssuedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset? NotBefore { get; }

    /// <inheritdoc />
    public DateTimeOffset? ExpiresAt { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Audiences => _audiences;

    /// <inheritdoc />
    public IReadOnlyList<IIdentityTokenClaim> Claims => _claims;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Properties { get; }

    /// <inheritdoc />
    public bool HasAudience(string audience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);

        for (var index = 0; index < _audiences.Length; index++)
        {
            if (string.Equals(_audiences[index], audience, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<IIdentityTokenClaim> GetClaims(string claimType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        List<IIdentityTokenClaim>? claims = null;

        for (var index = 0; index < _claims.Length; index++)
        {
            var claim = _claims[index];
            if (!string.Equals(claim.Type, claimType, StringComparison.Ordinal))
            {
                continue;
            }

            claims ??= new List<IIdentityTokenClaim>();
            claims.Add(claim);
        }

        return claims is null ? Array.Empty<IIdentityTokenClaim>() : claims.ToArray();
    }

    /// <inheritdoc />
    public bool TryGetClaim(string claimType, out IIdentityTokenClaim? claim)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        for (var index = 0; index < _claims.Length; index++)
        {
            if (!string.Equals(_claims[index].Type, claimType, StringComparison.Ordinal))
            {
                continue;
            }

            claim = _claims[index];
            return true;
        }

        claim = null;
        return false;
    }

    private static string[] CopyAudiences(IList<string> audiences)
    {
        ArgumentNullException.ThrowIfNull(audiences);

        var copy = new string[audiences.Count];
        for (var index = 0; index < audiences.Count; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(audiences[index]);
            copy[index] = audiences[index];
        }

        return copy;
    }

    private static IIdentityTokenClaim[] CopyClaims(IList<IdentityTokenClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var copy = new IIdentityTokenClaim[claims.Count];
        for (var index = 0; index < claims.Count; index++)
        {
            ArgumentNullException.ThrowIfNull(claims[index]);
            copy[index] = claims[index];
        }

        return copy;
    }
}
