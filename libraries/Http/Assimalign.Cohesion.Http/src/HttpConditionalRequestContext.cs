using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The inputs to a conditional-request evaluation (RFC 9110 &#167; 13): the request method, the
/// target resource's current validators, and the request's parsed precondition fields. Callers
/// populate this from an already-parsed request — reading the raw fields off the wire and mapping
/// them to these typed values is the consuming middleware's responsibility, not the protocol core's.
/// </summary>
/// <remarks>
/// A resource is considered to have a current representation when
/// <see cref="HasCurrentRepresentation"/> is <see langword="true"/> or either validator
/// (<see cref="ETag"/> / <see cref="LastModified"/>) is supplied — so setting a validator implies
/// existence and the flag is only needed for the rare validator-less-but-present resource.
/// </remarks>
public readonly struct HttpConditionalRequestContext
{
    /// <summary>
    /// Gets the request method. <c>GET</c> and <c>HEAD</c> are the read methods for which a failed
    /// <c>If-None-Match</c> / <c>If-Modified-Since</c> yields <c>304</c> rather than <c>412</c>.
    /// </summary>
    public required HttpMethod Method { get; init; }

    /// <summary>
    /// Gets the target resource's current entity-tag, or <see langword="null"/> when it has none.
    /// </summary>
    public HttpEntityTag? ETag { get; init; }

    /// <summary>
    /// Gets the target resource's current last-modified timestamp, or <see langword="null"/> when
    /// unknown.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// Gets a value indicating whether the target resource currently has a representation. This
    /// affects only the <c>*</c> wildcard of <see cref="IfMatch"/> / <see cref="IfNoneMatch"/>;
    /// supplying a validator already implies existence.
    /// </summary>
    public bool HasCurrentRepresentation { get; init; }

    /// <summary>
    /// Gets the parsed <c>If-Match</c> precondition, or <see langword="null"/> when the request has none.
    /// </summary>
    public HttpEntityTagCondition? IfMatch { get; init; }

    /// <summary>
    /// Gets the parsed <c>If-None-Match</c> precondition, or <see langword="null"/> when the request has none.
    /// </summary>
    public HttpEntityTagCondition? IfNoneMatch { get; init; }

    /// <summary>
    /// Gets the parsed <c>If-Modified-Since</c> timestamp, or <see langword="null"/> when absent or
    /// unparseable (an invalid date is ignored per RFC 9110 &#167; 13.1.3).
    /// </summary>
    public DateTimeOffset? IfModifiedSince { get; init; }

    /// <summary>
    /// Gets the parsed <c>If-Unmodified-Since</c> timestamp, or <see langword="null"/> when absent or
    /// unparseable (an invalid date is ignored per RFC 9110 &#167; 13.1.4).
    /// </summary>
    public DateTimeOffset? IfUnmodifiedSince { get; init; }

    /// <summary>
    /// Gets a value indicating whether the target resource currently has a representation, inferred
    /// from <see cref="HasCurrentRepresentation"/> or the presence of either validator.
    /// </summary>
    internal bool ResourceExists => HasCurrentRepresentation || ETag is not null || LastModified is not null;
}
