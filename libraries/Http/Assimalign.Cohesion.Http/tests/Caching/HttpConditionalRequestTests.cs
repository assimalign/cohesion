using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 13.2.2 compliance tests for <see cref="HttpConditionalRequest"/>: the precondition
/// evaluation order, the <c>If-None-Match</c>-over-<c>If-Modified-Since</c> precedence rule, and the
/// <c>304</c>-for-read / <c>412</c>-otherwise outcome split.
/// </summary>
public class HttpConditionalRequestTests
{
    private static readonly DateTimeOffset LastModified = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly HttpEntityTag CurrentTag = HttpEntityTag.Strong("v2");

    // ============================================================================
    // If-None-Match (§13.1.2)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-None-Match match on GET is 304")]
    public void Evaluate_IfNoneMatchMatchesOnGet_ShouldBeNotModified()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Get,
            ETag = CurrentTag,
            IfNoneMatch = HttpEntityTagCondition.Parse("\"v2\""),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.NotModified);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-None-Match match on PUT is 412")]
    public void Evaluate_IfNoneMatchMatchesOnPut_ShouldBePreconditionFailed()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Put,
            ETag = CurrentTag,
            IfNoneMatch = HttpEntityTagCondition.Parse("\"v2\""),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.PreconditionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-None-Match uses weak comparison")]
    public void Evaluate_IfNoneMatchWeakTag_ShouldMatch()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Get,
            ETag = CurrentTag,
            IfNoneMatch = HttpEntityTagCondition.Parse("W/\"v2\""),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.NotModified);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-None-Match wildcard on GET is 304")]
    public void Evaluate_IfNoneMatchWildcardWithRepresentation_ShouldBeNotModified()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Get,
            ETag = CurrentTag,
            IfNoneMatch = HttpEntityTagCondition.Parse("*"),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.NotModified);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-None-Match no match proceeds")]
    public void Evaluate_IfNoneMatchNoMatch_ShouldProceed()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Get,
            ETag = CurrentTag,
            IfNoneMatch = HttpEntityTagCondition.Parse("\"v1\""),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.Proceed);
    }

    // ============================================================================
    // If-Modified-Since (§13.1.3) and precedence
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-Modified-Since not modified is 304")]
    public void Evaluate_IfModifiedSinceNotModified_ShouldBeNotModified()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Get,
            LastModified = LastModified,
            IfModifiedSince = LastModified,
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.NotModified);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-Modified-Since modified proceeds")]
    public void Evaluate_IfModifiedSinceModified_ShouldProceed()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Get,
            LastModified = LastModified,
            IfModifiedSince = LastModified.AddSeconds(-60),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.Proceed);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-Modified-Since ignored for non-read method")]
    public void Evaluate_IfModifiedSinceOnPost_ShouldProceed()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Post,
            LastModified = LastModified,
            IfModifiedSince = LastModified,
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.Proceed);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-None-Match takes precedence over If-Modified-Since")]
    public void Evaluate_IfNoneMatchPresent_ShouldSuppressIfModifiedSince()
    {
        // If-None-Match does not match (v1 vs current v2) → proceed. If-Modified-Since would say
        // "not modified" (304) if it were evaluated, but a present If-None-Match suppresses it.
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Get,
            ETag = CurrentTag,
            LastModified = LastModified,
            IfNoneMatch = HttpEntityTagCondition.Parse("\"v1\""),
            IfModifiedSince = LastModified,
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.Proceed);
    }

    // ============================================================================
    // If-Match (§13.1.1) and If-Unmodified-Since (§13.1.4)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-Match match proceeds")]
    public void Evaluate_IfMatchMatches_ShouldProceed()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Put,
            ETag = CurrentTag,
            IfMatch = HttpEntityTagCondition.Parse("\"v2\""),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.Proceed);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-Match no match is 412")]
    public void Evaluate_IfMatchNoMatch_ShouldBePreconditionFailed()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Put,
            ETag = CurrentTag,
            IfMatch = HttpEntityTagCondition.Parse("\"v1\""),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.PreconditionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-Match uses strong comparison")]
    public void Evaluate_IfMatchWeakCurrentTag_ShouldFail()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Put,
            ETag = HttpEntityTag.Weak("v2"),
            IfMatch = HttpEntityTagCondition.Parse("\"v2\""),
        };

        // Weak current tag can never strongly match → 412.
        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.PreconditionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-Match wildcard without representation is 412")]
    public void Evaluate_IfMatchWildcardNoRepresentation_ShouldFail()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Put,
            HasCurrentRepresentation = false,
            IfMatch = HttpEntityTagCondition.Parse("*"),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.PreconditionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-Unmodified-Since modified is 412")]
    public void Evaluate_IfUnmodifiedSinceModified_ShouldBePreconditionFailed()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Put,
            LastModified = LastModified,
            IfUnmodifiedSince = LastModified.AddSeconds(-60),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.PreconditionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: If-Match present suppresses If-Unmodified-Since")]
    public void Evaluate_IfMatchPresent_ShouldSuppressIfUnmodifiedSince()
    {
        // If-Match matches → step 1 passes; If-Unmodified-Since (which would 412) is skipped because
        // If-Match is present.
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Put,
            ETag = CurrentTag,
            LastModified = LastModified,
            IfMatch = HttpEntityTagCondition.Parse("\"v2\""),
            IfUnmodifiedSince = LastModified.AddSeconds(-60),
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.Proceed);
    }

    // ============================================================================
    // No preconditions
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpConditionalRequest: no preconditions proceeds")]
    public void Evaluate_NoPreconditions_ShouldProceed()
    {
        var context = new HttpConditionalRequestContext
        {
            Method = HttpMethod.Get,
            ETag = CurrentTag,
            LastModified = LastModified,
        };

        HttpConditionalRequest.Evaluate(context).ShouldBe(HttpPreconditionOutcome.Proceed);
    }
}
