using System;

namespace Assimalign.Cohesion.Web.Query;

using Assimalign.Cohesion.Http;

/// <summary>
/// Conditional-QUERY helpers (RFC 10008 &#167; 2.6): evaluate a QUERY request's precondition
/// fields exactly as for the equivalent conditional GET, reusing the core
/// <see cref="HttpConditionalRequest"/> evaluator, and shape the <c>304</c> / <c>412</c> outcome
/// imperatively onto the response.
/// </summary>
/// <remarks>
/// <para>
/// The helpers read the four header preconditions (<c>If-Match</c>, <c>If-None-Match</c>,
/// <c>If-Modified-Since</c>, <c>If-Unmodified-Since</c>) off the request, hand them to
/// <see cref="HttpConditionalRequest.Evaluate"/> together with the supplied resource validators,
/// and — because the core evaluator classifies QUERY as a read method per RFC 10008 &#167; 2.6 —
/// obtain <c>304 Not Modified</c> where the equivalent GET would, and
/// <c>412 Precondition Failed</c> otherwise. A malformed precondition field is ignored (treated
/// as absent), matching the RFC 9110 &#167; 13.1.3 posture the core date parsing already encodes.
/// </para>
/// <para>
/// Call these <em>before</em> executing the query: a precondition that yields <c>304</c> or
/// <c>412</c> means the method must not be performed (RFC 9110 &#167; 13.1.2).
/// </para>
/// </remarks>
public static class HttpContextQueryExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Evaluates the request's precondition fields against <paramref name="validators"/> per
        /// RFC 9110 &#167; 13.2.2, with QUERY treated as a read method (RFC 10008 &#167; 2.6).
        /// </summary>
        /// <param name="validators">The target resource's current validators.</param>
        /// <returns>
        /// <see cref="HttpPreconditionOutcome.Proceed"/> when the query should run,
        /// <see cref="HttpPreconditionOutcome.NotModified"/> when a <c>304</c> should be sent, or
        /// <see cref="HttpPreconditionOutcome.PreconditionFailed"/> when a <c>412</c> should be sent.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
        public HttpPreconditionOutcome EvaluateQueryPreconditions(in WebQueryResourceValidators validators)
        {
            ArgumentNullException.ThrowIfNull(context);

            IHttpHeaderCollection headers = context.Request.Headers;

            var evaluation = new HttpConditionalRequestContext
            {
                Method = context.Request.Method,
                ETag = validators.ETag,
                LastModified = validators.LastModified,
                HasCurrentRepresentation = validators.HasCurrentRepresentation,
                IfMatch = ParseEntityTagCondition(headers, HttpHeaderKey.IfMatch),
                IfNoneMatch = ParseEntityTagCondition(headers, HttpHeaderKey.IfNoneMatch),
                IfModifiedSince = ParseDate(headers, HttpHeaderKey.IfModifiedSince),
                IfUnmodifiedSince = ParseDate(headers, HttpHeaderKey.IfUnmodifiedSince),
            };

            return HttpConditionalRequest.Evaluate(in evaluation);
        }

        /// <summary>
        /// Evaluates the request's preconditions and, when they resolve the exchange, writes the
        /// outcome: <c>304 Not Modified</c> carrying the supplied validators, or
        /// <c>412 Precondition Failed</c>. The response body is left untouched — both statuses are
        /// bodiless by this helper's construction.
        /// </summary>
        /// <param name="validators">The target resource's current validators.</param>
        /// <returns>
        /// <see langword="true"/> when a <c>304</c> or <c>412</c> was written and the caller must
        /// not execute the query; <see langword="false"/> when the query should proceed.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="context"/> is <see langword="null"/>.</exception>
        public bool TryHandleQueryPreconditions(in WebQueryResourceValidators validators)
        {
            ArgumentNullException.ThrowIfNull(context);

            switch (context.EvaluateQueryPreconditions(in validators))
            {
                case HttpPreconditionOutcome.NotModified:
                    context.Response.StatusCode = HttpStatusCode.NotModified;
                    // RFC 9110 §15.4.5: a 304 carries the validator fields the 200 would have.
                    SetValidatorHeaders(context.Response, in validators);
                    return true;

                case HttpPreconditionOutcome.PreconditionFailed:
                    context.Response.StatusCode = HttpStatusCode.PreconditionFailed;
                    return true;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Stamps the resource's validator fields (<c>ETag</c>, <c>Last-Modified</c>) onto the
    /// response so clients can issue subsequent conditional requests.
    /// </summary>
    /// <param name="response">The response to stamp.</param>
    /// <param name="validators">The validators to emit; absent members are skipped.</param>
    internal static void SetValidatorHeaders(IHttpResponse response, in WebQueryResourceValidators validators)
    {
        if (validators.ETag is { } etag)
        {
            response.Headers[HttpHeaderKey.ETag] = etag.ToString();
        }
        if (validators.LastModified is { } lastModified)
        {
            response.Headers[HttpHeaderKey.LastModified] = HttpDate.Format(lastModified);
        }
    }

    private static HttpEntityTagCondition? ParseEntityTagCondition(IHttpHeaderCollection headers, HttpHeaderKey key)
        => headers.TryGetValue(key, out HttpHeaderValue value) && HttpEntityTagCondition.TryParse(value.Value, out HttpEntityTagCondition condition)
            ? condition
            : null;

    private static DateTimeOffset? ParseDate(IHttpHeaderCollection headers, HttpHeaderKey key)
        => headers.TryGetValue(key, out HttpHeaderValue value) && HttpDate.TryParse(value.Value, out DateTimeOffset date)
            ? date
            : null;
}
