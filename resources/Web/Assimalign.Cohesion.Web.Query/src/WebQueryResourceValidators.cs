using System;

namespace Assimalign.Cohesion.Web.Query;

using Assimalign.Cohesion.Http;

/// <summary>
/// The target resource's current validators for a conditional QUERY evaluation
/// (RFC 10008 &#167; 2.6): the entity-tag and last-modified timestamp of the representation the
/// equivalent GET would select, plus the existence flag the <c>*</c> wildcard preconditions
/// consult.
/// </summary>
/// <remarks>
/// Supplying either validator already implies the resource has a current representation;
/// <see cref="HasCurrentRepresentation"/> exists for the rare resource that exists without
/// carrying a validator (mirroring <see cref="HttpConditionalRequestContext"/>).
/// </remarks>
public readonly struct WebQueryResourceValidators
{
    /// <summary>
    /// Gets the current entity-tag of the representation the equivalent GET would select, or
    /// <see langword="null"/> when the resource has none.
    /// </summary>
    public HttpEntityTag? ETag { get; init; }

    /// <summary>
    /// Gets the current last-modified timestamp of the selected representation, or
    /// <see langword="null"/> when unknown.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// Gets a value indicating whether the target resource currently has a representation. Only
    /// consulted by the <c>*</c> wildcard of <c>If-Match</c> / <c>If-None-Match</c>; supplying a
    /// validator already implies existence.
    /// </summary>
    public bool HasCurrentRepresentation { get; init; }
}
