using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9111 &#167; 5.2 compliance tests for <see cref="HttpCacheControl"/>: request/response directive
/// parsing, delta-seconds handling, <c>no-cache</c>/<c>private</c> field-list and quoted-string
/// arguments, extension directives, malformed-input rejection, and <see cref="object.ToString"/>
/// round-tripping.
/// </summary>
public class HttpCacheControlTests
{
    // ============================================================================
    // Boolean directives
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: response boolean directives parse")]
    public void TryParse_ResponseBooleanDirectives_ShouldSetFlags()
    {
        HttpCacheControl.TryParse("public, no-transform, must-revalidate, proxy-revalidate, immutable, must-understand", out HttpCacheControl cc).ShouldBeTrue();

        cc.Public.ShouldBeTrue();
        cc.NoTransform.ShouldBeTrue();
        cc.MustRevalidate.ShouldBeTrue();
        cc.ProxyRevalidate.ShouldBeTrue();
        cc.Immutable.ShouldBeTrue();
        cc.MustUnderstand.ShouldBeTrue();
        cc.NoStore.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: request directives parse")]
    public void TryParse_RequestDirectives_ShouldParse()
    {
        HttpCacheControl.TryParse("no-cache, no-store, only-if-cached, min-fresh=30", out HttpCacheControl cc).ShouldBeTrue();

        cc.NoCache.ShouldBeTrue();
        cc.NoStore.ShouldBeTrue();
        cc.OnlyIfCached.ShouldBeTrue();
        cc.MinFresh.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: directive names are case-insensitive")]
    public void TryParse_MixedCaseNames_ShouldParse()
    {
        HttpCacheControl.TryParse("Max-Age=60, No-Store", out HttpCacheControl cc).ShouldBeTrue();

        cc.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        cc.NoStore.ShouldBeTrue();
    }

    // ============================================================================
    // Delta-seconds
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: max-age and s-maxage parse")]
    public void TryParse_MaxAgeAndSharedMaxAge_ShouldParse()
    {
        HttpCacheControl.TryParse("max-age=3600, s-maxage=7200", out HttpCacheControl cc).ShouldBeTrue();

        cc.MaxAge.ShouldBe(TimeSpan.FromHours(1));
        cc.SharedMaxAge.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: quoted delta-seconds tolerated")]
    public void TryParse_QuotedDeltaSeconds_ShouldParse()
    {
        HttpCacheControl.TryParse("max-age=\"600\"", out HttpCacheControl cc).ShouldBeTrue();

        cc.MaxAge.ShouldBe(TimeSpan.FromSeconds(600));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: overflowing delta-seconds clamps")]
    public void TryParse_OverflowingDeltaSeconds_ShouldClamp()
    {
        HttpCacheControl.TryParse("max-age=99999999999999999999", out HttpCacheControl cc).ShouldBeTrue();

        cc.MaxAge.ShouldBe(TimeSpan.FromSeconds(int.MaxValue));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: stale-while/if directives parse")]
    public void TryParse_StaleDirectives_ShouldParse()
    {
        HttpCacheControl.TryParse("max-age=0, stale-while-revalidate=10, stale-if-error=20", out HttpCacheControl cc).ShouldBeTrue();

        cc.MaxAge.ShouldBe(TimeSpan.Zero);
        cc.StaleWhileRevalidate.ShouldBe(TimeSpan.FromSeconds(10));
        cc.StaleIfError.ShouldBe(TimeSpan.FromSeconds(20));
    }

    // ============================================================================
    // max-stale (value optional)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: max-stale with value")]
    public void TryParse_MaxStaleWithValue_ShouldParse()
    {
        HttpCacheControl.TryParse("max-stale=120", out HttpCacheControl cc).ShouldBeTrue();

        cc.HasMaxStale.ShouldBeTrue();
        cc.MaxStale.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: bare max-stale means any staleness")]
    public void TryParse_BareMaxStale_ShouldAcceptAnyStaleness()
    {
        HttpCacheControl.TryParse("max-stale", out HttpCacheControl cc).ShouldBeTrue();

        cc.HasMaxStale.ShouldBeTrue();
        cc.MaxStale.ShouldBeNull();
    }

    // ============================================================================
    // no-cache / private field-list arguments (quoted-string)
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: no-cache field list parses")]
    public void TryParse_NoCacheFieldList_ShouldParse()
    {
        HttpCacheControl.TryParse("no-cache=\"Set-Cookie, Authorization\"", out HttpCacheControl cc).ShouldBeTrue();

        cc.NoCache.ShouldBeTrue();
        cc.NoCacheFields.Count.ShouldBe(2);
        cc.NoCacheFields[0].ShouldBe("Set-Cookie");
        cc.NoCacheFields[1].ShouldBe("Authorization");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: private field list parses")]
    public void TryParse_PrivateFieldList_ShouldParse()
    {
        HttpCacheControl.TryParse("private=\"X-Personal\"", out HttpCacheControl cc).ShouldBeTrue();

        cc.Private.ShouldBeTrue();
        cc.PrivateFields.Count.ShouldBe(1);
        cc.PrivateFields[0].ShouldBe("X-Personal");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: bare no-cache has no fields")]
    public void TryParse_BareNoCache_ShouldHaveNoFields()
    {
        HttpCacheControl.TryParse("no-cache", out HttpCacheControl cc).ShouldBeTrue();

        cc.NoCache.ShouldBeTrue();
        cc.NoCacheFields.Count.ShouldBe(0);
    }

    // ============================================================================
    // Extension directives
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: unknown directive preserved as extension")]
    public void TryParse_UnknownDirective_ShouldPreserveExtension()
    {
        HttpCacheControl.TryParse("max-age=60, community=\"UCI\", custom-flag", out HttpCacheControl cc).ShouldBeTrue();

        cc.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        cc.Extensions.Count.ShouldBe(2);
        cc.Extensions[0].Name.ShouldBe("community");
        cc.Extensions[0].Value.ShouldBe("UCI");
        cc.Extensions[1].Name.ShouldBe("custom-flag");
        cc.Extensions[1].Value.ShouldBeNull();
    }

    // ============================================================================
    // Malformed / edge cases
    // ============================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("max-age=abc")]        // non-numeric delta-seconds
    [InlineData("max-age=")]           // missing required value
    [InlineData("max-age=-5")]         // negative is not a delta-seconds digit sequence
    [InlineData("s-maxage=1.5")]       // fractional is not a delta-seconds
    [InlineData("max age=1")]          // space in directive name
    [InlineData(",,")]                 // only empty elements
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        HttpCacheControl.TryParse(raw, out HttpCacheControl cc).ShouldBeFalse();
        cc.IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: empty list elements are ignored")]
    public void TryParse_EmptyElements_ShouldBeIgnored()
    {
        HttpCacheControl.TryParse("max-age=60, , no-store,", out HttpCacheControl cc).ShouldBeTrue();

        cc.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        cc.NoStore.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: Parse throws on malformed")]
    public void Parse_Malformed_ShouldThrowHttpException()
    {
        Should.Throw<HttpException>(() => HttpCacheControl.Parse("max-age=oops"));
    }

    // ============================================================================
    // Round-trip
    // ============================================================================

    // Inputs are already in the canonical emission order so ToString reproduces them verbatim.
    [Theory]
    [InlineData("no-store")]
    [InlineData("no-store, no-cache, must-revalidate")]
    [InlineData("public, max-age=3600")]
    [InlineData("private, max-age=0, s-maxage=60")]
    [InlineData("max-stale")]
    [InlineData("only-if-cached, max-stale=120")]
    [InlineData("no-cache=\"Set-Cookie, Authorization\"")]
    [InlineData("immutable, max-age=31536000")]
    [InlineData("max-age=60, community=UCI")]
    public void ToString_ThenParse_ShouldRoundTrip(string canonical)
    {
        HttpCacheControl.TryParse(canonical, out HttpCacheControl first).ShouldBeTrue();

        string rendered = first.ToString();
        rendered.ShouldBe(canonical);

        HttpCacheControl.TryParse(rendered, out HttpCacheControl second).ShouldBeTrue();
        second.ToString().ShouldBe(canonical);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControl: default value is empty")]
    public void Default_ShouldBeEmpty()
    {
        HttpCacheControl cc = default;

        cc.IsEmpty.ShouldBeTrue();
        cc.ToString().ShouldBe("");
    }
}
