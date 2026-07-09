using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpAcceptQueryTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - AcceptQuery: Should parse the RFC 10008 §3 example list of media ranges")]
    public void TryParse_Rfc10008Example_ShouldProjectEachMediaRange()
    {
        // RFC 10008 §3 example — a String member and a Token member with a parameter.
        const string value = "\"application/jsonpath\", application/sql;charset=\"UTF-8\"";

        bool parsed = HttpAcceptQuery.TryParse(value, out HttpAcceptQuery acceptQuery);

        parsed.ShouldBeTrue();
        acceptQuery.Count.ShouldBe(2);
        acceptQuery.MediaRanges[0].ShouldBe(new HttpMediaType("application/jsonpath"));
        acceptQuery.MediaRanges[1].Type.ShouldBe("application");
        acceptQuery.MediaRanges[1].SubType.ShouldBe("sql");
        acceptQuery.MediaRanges[1].Charset.ShouldBe("UTF-8");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - AcceptQuery: Should accept a content type matched by an advertised range")]
    public void Accepts_ContentTypeWithinAdvertisedRange_ShouldReturnTrue()
    {
        HttpAcceptQuery acceptQuery = HttpAcceptQuery.Parse("text/*, application/sql");

        acceptQuery.Accepts(new HttpMediaType("text/csv")).ShouldBeTrue();
        acceptQuery.Accepts(new HttpMediaType("application/sql")).ShouldBeTrue();
        acceptQuery.Accepts(new HttpMediaType("application/json")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - AcceptQuery: Should round-trip parse then serialize to an equivalent value")]
    public void Serialize_AfterParse_ShouldRoundTrip()
    {
        const string value = "application/sql;charset=\"UTF-8\", application/graphql";

        HttpAcceptQuery original = HttpAcceptQuery.Parse(value);
        string serialized = original.Serialize();
        HttpAcceptQuery reparsed = HttpAcceptQuery.Parse(serialized);

        reparsed.ShouldBe(original);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - AcceptQuery: Should serialize a constructed value to canonical structured-field form")]
    public void Serialize_ConstructedValue_ShouldEmitCanonicalList()
    {
        HttpAcceptQuery acceptQuery = new(new[]
        {
            new HttpMediaType("application/sql; charset=utf-8"),
            new HttpMediaType("application/graphql"),
        });

        acceptQuery.Serialize().ShouldBe("application/sql;charset=\"utf-8\", application/graphql");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - AcceptQuery: Should parse via the header-value overload")]
    public void TryParse_HeaderValueOverload_ShouldParse()
    {
        HttpHeaderValue headerValue = new("application/sql");

        HttpAcceptQuery.TryParse(headerValue, out HttpAcceptQuery acceptQuery).ShouldBeTrue();
        acceptQuery.Count.ShouldBe(1);
        acceptQuery.MediaRanges[0].ShouldBe(new HttpMediaType("application/sql"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - AcceptQuery: Should treat an empty field as no advertised ranges")]
    public void TryParse_EmptyValue_ShouldYieldEmpty()
    {
        HttpAcceptQuery.TryParse("", out HttpAcceptQuery acceptQuery).ShouldBeTrue();
        acceptQuery.IsEmpty.ShouldBeTrue();
        acceptQuery.Count.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - AcceptQuery: Should reject members that are not media ranges")]
    [InlineData("42")]                      // a bare Integer is not a media range
    [InlineData("notamediatype")]           // a Token without a type/subtype is not a media type
    [InlineData("(application/sql text/*)")] // an inner list has no media-range meaning
    [InlineData("application/sql;;")]        // malformed structured-field list
    public void TryParse_NonMediaRangeMember_ShouldReturnFalse(string value)
    {
        HttpAcceptQuery.TryParse(value, out HttpAcceptQuery acceptQuery).ShouldBeFalse();
        acceptQuery.IsEmpty.ShouldBeTrue();
    }
}
