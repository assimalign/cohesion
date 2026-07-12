using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Evaluates a request's conditional preconditions against a target resource's current validators,
/// implementing the RFC 9110 &#167; 13.2.2 evaluation order for the header preconditions
/// <c>If-Match</c>, <c>If-Unmodified-Since</c>, <c>If-None-Match</c>, and <c>If-Modified-Since</c>.
/// </summary>
/// <remarks>
/// <para>
/// The evaluator is a pure function over value types — the same rationale that makes
/// <c>HttpContentNegotiation</c> a static helper rather than an injected service. It assumes the
/// caller would otherwise produce a <c>2xx</c> response; the &#167; 13.2.2 rule that all
/// preconditions are ignored when the response status would be something else is the caller's to
/// apply. The <c>If-Range</c> precondition (&#167; 13.2.2 step 5) is range-specific and layered by the
/// range-request primitives, which reuse <see cref="HttpEntityTagCondition"/> and this type's
/// comparison helpers rather than re-deriving them.
/// </para>
/// <para>
/// The ordering encodes two rules the acceptance criteria call out: <c>If-None-Match</c> takes
/// precedence over <c>If-Modified-Since</c> (a present <c>If-None-Match</c> suppresses the
/// <c>If-Modified-Since</c> check), and a failed read precondition yields <c>304</c> for the read
/// methods but <c>412</c> for any other method. The read methods are <c>GET</c> and <c>HEAD</c>
/// (RFC 9110 &#167; 13.1.2) plus <c>QUERY</c> — RFC 10008 &#167; 2.6 requires a conditional QUERY to
/// be evaluated exactly as the equivalent conditional GET, selecting the same representation and
/// producing <c>304</c> where a GET would.
/// </para>
/// </remarks>
public static class HttpConditionalRequest
{
    /// <summary>
    /// Evaluates the conditional preconditions in <paramref name="context"/> per RFC 9110
    /// &#167; 13.2.2.
    /// </summary>
    /// <param name="context">The request method, resource validators, and parsed precondition fields.</param>
    /// <returns>
    /// <see cref="HttpPreconditionOutcome.Proceed"/> when the method should run,
    /// <see cref="HttpPreconditionOutcome.NotModified"/> when a <c>304</c> should be sent, or
    /// <see cref="HttpPreconditionOutcome.PreconditionFailed"/> when a <c>412</c> should be sent.
    /// </returns>
    public static HttpPreconditionOutcome Evaluate(in HttpConditionalRequestContext context)
    {
        bool exists = context.ResourceExists;
        bool isRead = IsReadMethod(context.Method);

        // Step 1 — If-Match (strong comparison; RFC 9110 §13.1.1). If present and it fails, 412.
        if (context.IfMatch is { } ifMatch)
        {
            if (!ifMatch.MatchesStrong(context.ETag, exists))
            {
                return HttpPreconditionOutcome.PreconditionFailed;
            }
        }
        // Step 2 — If-Unmodified-Since, only when If-Match is absent (RFC 9110 §13.1.4). If the
        // resource was modified after the supplied date, 412. A resource with no known
        // last-modified time cannot fail this guard.
        else if (context.IfUnmodifiedSince is { } ifUnmodifiedSince)
        {
            if (context.LastModified is { } lastModified && lastModified > ifUnmodifiedSince)
            {
                return HttpPreconditionOutcome.PreconditionFailed;
            }
        }

        // Step 3 — If-None-Match (weak comparison; RFC 9110 §13.1.2). A match means the client's
        // representation is current: 304 for read methods, 412 otherwise. A present If-None-Match
        // suppresses the If-Modified-Since check regardless of the result.
        if (context.IfNoneMatch is { } ifNoneMatch)
        {
            if (ifNoneMatch.MatchesWeak(context.ETag, exists))
            {
                return isRead ? HttpPreconditionOutcome.NotModified : HttpPreconditionOutcome.PreconditionFailed;
            }
        }
        // Step 4 — If-Modified-Since, only for read methods and only when If-None-Match is absent
        // (RFC 9110 §13.1.3). If the resource was not modified since the supplied date, 304.
        else if (isRead && context.IfModifiedSince is { } ifModifiedSince)
        {
            if (context.LastModified is { } lastModified && lastModified <= ifModifiedSince)
            {
                return HttpPreconditionOutcome.NotModified;
            }
        }

        return HttpPreconditionOutcome.Proceed;
    }

    private static bool IsReadMethod(HttpMethod method)
        => method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Query;
}
