using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents an immutable, insertion-ordered collection of normalized claims.
/// </summary>
/// <remarks>
/// Duplicate claim types are legal and meaningful: multi-value data is canonically
/// represented as multiple claims sharing one type, so <see cref="GetAll" /> is the
/// multi-value accessor. Claim types compare ordinally. Lookup members follow the
/// Try/Get vocabulary that the wider IdentityModel family standardizes on.
/// </remarks>
public interface IIdentityClaimCollection : IReadOnlyList<IIdentityClaim>
{
    /// <summary>
    /// Determines whether at least one claim of the provided type exists.
    /// </summary>
    /// <param name="claimType">The canonical claim type to match.</param>
    /// <returns><see langword="true" /> when a matching claim exists; otherwise <see langword="false" />.</returns>
    /// <exception cref="System.ArgumentException">Thrown when <paramref name="claimType" /> is null or whitespace.</exception>
    bool Contains(string claimType);

    /// <summary>
    /// Attempts to locate the first claim of the provided type.
    /// </summary>
    /// <param name="claimType">The canonical claim type to match.</param>
    /// <param name="claim">When this method returns, contains the first matching claim, if one exists.</param>
    /// <returns><see langword="true" /> when a matching claim exists; otherwise <see langword="false" />.</returns>
    /// <exception cref="System.ArgumentException">Thrown when <paramref name="claimType" /> is null or whitespace.</exception>
    bool TryGet(string claimType, [NotNullWhen(true)] out IIdentityClaim? claim);

    /// <summary>
    /// Gets every claim of the provided type, in insertion order.
    /// </summary>
    /// <param name="claimType">The canonical claim type to match.</param>
    /// <returns>The matching claims, or an empty list.</returns>
    /// <exception cref="System.ArgumentException">Thrown when <paramref name="claimType" /> is null or whitespace.</exception>
    IReadOnlyList<IIdentityClaim> GetAll(string claimType);

    /// <summary>
    /// Gets every value carried by claims of the provided type, expanding array-valued
    /// claims one level so that duplicate-claim and array-claim representations of
    /// multi-value data yield the same flattened sequence. Elements of a nested array
    /// remain array-valued; only the outermost level expands.
    /// </summary>
    /// <param name="claimType">The canonical claim type to match.</param>
    /// <returns>The flattened values in insertion order, or an empty list.</returns>
    /// <exception cref="System.ArgumentException">Thrown when <paramref name="claimType" /> is null or whitespace.</exception>
    IReadOnlyList<IdentityClaimValue> GetValues(string claimType);
}
