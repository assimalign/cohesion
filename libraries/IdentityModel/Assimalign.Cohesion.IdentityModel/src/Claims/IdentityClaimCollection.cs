using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents an immutable, insertion-ordered snapshot of normalized claims.
/// </summary>
public sealed class IdentityClaimCollection : IIdentityClaimCollection
{
    private readonly IIdentityClaim[] _claims;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityClaimCollection" /> class by
    /// snapshotting the provided claims.
    /// </summary>
    /// <param name="claims">The claims to snapshot, in order.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="claims" /> or any element is null.
    /// </exception>
    public IdentityClaimCollection(IEnumerable<IIdentityClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var snapshot = new List<IIdentityClaim>(claims is IReadOnlyCollection<IIdentityClaim> sized ? sized.Count : 4);
        foreach (var claim in claims)
        {
            ArgumentNullException.ThrowIfNull(claim, nameof(claims));
            snapshot.Add(claim);
        }

        _claims = snapshot.ToArray();
    }

    /// <summary>
    /// Gets the empty claim collection.
    /// </summary>
    public static IdentityClaimCollection Empty { get; } = new(Array.Empty<IIdentityClaim>());

    /// <inheritdoc />
    public IIdentityClaim this[int index] => _claims[index];

    /// <inheritdoc />
    public int Count => _claims.Length;

    /// <inheritdoc />
    public bool Contains(string claimType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        for (var index = 0; index < _claims.Length; index++)
        {
            if (string.Equals(_claims[index].Type, claimType, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool TryGet(string claimType, [NotNullWhen(true)] out IIdentityClaim? claim)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        for (var index = 0; index < _claims.Length; index++)
        {
            if (string.Equals(_claims[index].Type, claimType, StringComparison.Ordinal))
            {
                claim = _claims[index];
                return true;
            }
        }

        claim = null;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<IIdentityClaim> GetAll(string claimType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        // The list is a fresh per-call instance, so returning it leaks no interior state.
        List<IIdentityClaim>? matches = null;
        for (var index = 0; index < _claims.Length; index++)
        {
            if (string.Equals(_claims[index].Type, claimType, StringComparison.Ordinal))
            {
                matches ??= new List<IIdentityClaim>();
                matches.Add(_claims[index]);
            }
        }

        return matches is null ? Array.Empty<IIdentityClaim>() : matches;
    }

    /// <inheritdoc />
    public IReadOnlyList<IdentityClaimValue> GetValues(string claimType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        // The list is a fresh per-call instance, so returning it leaks no interior state.
        List<IdentityClaimValue>? values = null;
        for (var index = 0; index < _claims.Length; index++)
        {
            if (!string.Equals(_claims[index].Type, claimType, StringComparison.Ordinal))
            {
                continue;
            }

            values ??= new List<IdentityClaimValue>();

            var value = _claims[index].Value;
            if (value.TryGetArray(out var elements))
            {
                values.AddRange(elements);
            }
            else
            {
                values.Add(value);
            }
        }

        return values is null ? Array.Empty<IdentityClaimValue>() : values;
    }

    /// <inheritdoc />
    public IEnumerator<IIdentityClaim> GetEnumerator()
    {
        for (var index = 0; index < _claims.Length; index++)
        {
            yield return _claims[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
