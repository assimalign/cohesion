using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// End-to-end RFC 9110 &#167; 13.2.2 composition tests: the range/precondition primitives layer the
/// <c>If-Range</c> range-application decision (step 5, <see cref="HttpIfRange.Matches"/>) and range
/// selection (<see cref="HttpRangeSelector"/>) on top of the shared conditional-request evaluator
/// (steps 1&#8211;4, <see cref="HttpConditionalRequest.Evaluate"/>). These tests exercise the full
/// consumer flow — the typed decision of proceed / <c>304</c> / <c>412</c> / <c>206</c> / <c>416</c> /
/// ignore-range — through that composition.
/// </summary>
public class HttpRangePreconditionCompositionTests
{
    private static readonly DateTimeOffset LastModified = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HttpEntityTag CurrentTag = HttpEntityTag.Strong("v1");
    private const long ContentLength = 1000;

    private enum ResponseKind
    {
        Ok200,
        Partial206,
        NotModified304,
        PreconditionFailed412,
        RangeNotSatisfiable416,
    }

    /// <summary>
    /// The canonical consumer composition: evaluate steps 1&#8211;4, then (only on proceed) apply the
    /// <c>If-Range</c> step-5 decision and range selection.
    /// </summary>
    private static ResponseKind Decide(
        HttpMethod method,
        HttpEntityTagCondition? ifMatch = null,
        HttpEntityTagCondition? ifNoneMatch = null,
        DateTimeOffset? ifModifiedSince = null,
        DateTimeOffset? ifUnmodifiedSince = null,
        HttpRangeHeader? range = null,
        HttpIfRange? ifRange = null)
    {
        HttpPreconditionOutcome outcome = HttpConditionalRequest.Evaluate(new HttpConditionalRequestContext
        {
            Method = method,
            ETag = CurrentTag,
            LastModified = LastModified,
            IfMatch = ifMatch,
            IfNoneMatch = ifNoneMatch,
            IfModifiedSince = ifModifiedSince,
            IfUnmodifiedSince = ifUnmodifiedSince,
        });

        switch (outcome)
        {
            case HttpPreconditionOutcome.NotModified:
                return ResponseKind.NotModified304;
            case HttpPreconditionOutcome.PreconditionFailed:
                return ResponseKind.PreconditionFailed412;
        }

        if (range is not HttpRangeHeader rangeHeader)
        {
            return ResponseKind.Ok200;
        }

        // Step 5: honor the range only when there is no If-Range or its validator still matches.
        bool applyRange = ifRange is not HttpIfRange gate || gate.Matches(CurrentTag, LastModified);
        if (!applyRange)
        {
            return ResponseKind.Ok200;
        }

        HttpRangeSelection selection = HttpRangeSelector.Select(rangeHeader, ContentLength);
        return selection.Status switch
        {
            HttpRangeSelectionStatus.Partial => ResponseKind.Partial206,
            HttpRangeSelectionStatus.Unsatisfiable => ResponseKind.RangeNotSatisfiable416,
            _ => ResponseKind.Ok200,
        };
    }

    [Fact]
    public void Get_IfNoneMatchMatches_ShouldBe304_BeforeAnyRange()
    {
        Decide(HttpMethod.Get, ifNoneMatch: HttpEntityTagCondition.Parse("\"v1\""), range: HttpRangeHeader.Parse("bytes=0-99"))
            .ShouldBe(ResponseKind.NotModified304);
    }

    [Fact]
    public void Put_IfMatchFails_ShouldBe412()
    {
        Decide(HttpMethod.Put, ifMatch: HttpEntityTagCondition.Parse("\"v2\""))
            .ShouldBe(ResponseKind.PreconditionFailed412);
    }

    [Fact]
    public void Get_RangeNoIfRangeSatisfiable_ShouldBe206()
    {
        Decide(HttpMethod.Get, range: HttpRangeHeader.Parse("bytes=0-99"))
            .ShouldBe(ResponseKind.Partial206);
    }

    [Fact]
    public void Get_RangeUnsatisfiable_ShouldBe416()
    {
        Decide(HttpMethod.Get, range: HttpRangeHeader.Parse("bytes=5000-6000"))
            .ShouldBe(ResponseKind.RangeNotSatisfiable416);
    }

    [Fact]
    public void Get_IfRangeMatches_ShouldApplyRange206()
    {
        Decide(HttpMethod.Get, range: HttpRangeHeader.Parse("bytes=0-99"), ifRange: HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1")))
            .ShouldBe(ResponseKind.Partial206);
    }

    [Fact]
    public void Get_IfRangeMismatch_ShouldIgnoreRangeAndServe200()
    {
        Decide(HttpMethod.Get, range: HttpRangeHeader.Parse("bytes=0-99"), ifRange: HttpIfRange.FromEntityTag(HttpEntityTag.Strong("stale")))
            .ShouldBe(ResponseKind.Ok200);
    }

    [Fact]
    public void Get_NoConditionsNoRange_ShouldBe200()
    {
        Decide(HttpMethod.Get).ShouldBe(ResponseKind.Ok200);
    }
}
