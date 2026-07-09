using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Policies;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// Covers typed route-value conversion and the expanded inline constraint catalog (#789): each type
/// constraint parses once and surfaces a strongly-typed value, and the length/min/max validators
/// govern the raw text while leaving it a string.
/// </summary>
public class RouteConstraintTests
{
    private static bool TryMatch(string template, string path, out RouteValueDictionary values)
    {
        Route route = new(HttpMethod.Get, template);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, path);
        return route.TryMatch(context, out values);
    }

    // ---- Typed conversions --------------------------------------------------------------------

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint int: converts to a typed Int32")]
    public void Int_OnNumericValue_ShouldConvertToTypedInt()
    {
        TryMatch("/users/{id:int}", "/users/42", out RouteValueDictionary values).ShouldBeTrue();
        values["id"].ShouldBeOfType<int>().ShouldBe(42);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Constraint int: rejects non-integers and overflow")]
    [InlineData("/users/abc")]
    [InlineData("/users/4.2")]
    [InlineData("/users/99999999999")] // overflows Int32 — a regex would have accepted it
    public void Int_OnInvalidValue_ShouldReject(string path)
    {
        TryMatch("/users/{id:int}", path, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint long: converts to a typed Int64")]
    public void Long_OnNumericValue_ShouldConvertToTypedLong()
    {
        TryMatch("/users/{id:long}", "/users/99999999999", out RouteValueDictionary values).ShouldBeTrue();
        values["id"].ShouldBeOfType<long>().ShouldBe(99999999999L);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint decimal: converts to a typed Decimal (invariant culture)")]
    public void Decimal_OnNumericValue_ShouldConvertToTypedDecimal()
    {
        TryMatch("/price/{amount:decimal}", "/price/3.14", out RouteValueDictionary values).ShouldBeTrue();
        values["amount"].ShouldBeOfType<decimal>().ShouldBe(3.14m);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint double: converts to a typed Double")]
    public void Double_OnNumericValue_ShouldConvertToTypedDouble()
    {
        TryMatch("/x/{v:double}", "/x/3.5", out RouteValueDictionary values).ShouldBeTrue();
        values["v"].ShouldBeOfType<double>().ShouldBe(3.5d);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint float: converts to a typed Single")]
    public void Float_OnNumericValue_ShouldConvertToTypedFloat()
    {
        TryMatch("/x/{v:float}", "/x/2.5", out RouteValueDictionary values).ShouldBeTrue();
        values["v"].ShouldBeOfType<float>().ShouldBe(2.5f);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Constraint bool: converts to a typed Boolean")]
    [InlineData("/flag/true", true)]
    [InlineData("/flag/false", false)]
    [InlineData("/flag/TRUE", true)]
    public void Bool_OnBooleanValue_ShouldConvertToTypedBool(string path, bool expected)
    {
        TryMatch("/flag/{on:bool}", path, out RouteValueDictionary values).ShouldBeTrue();
        values["on"].ShouldBeOfType<bool>().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint bool: rejects non-booleans")]
    public void Bool_OnNonBoolean_ShouldReject()
    {
        TryMatch("/flag/{on:bool}", "/flag/yes", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint guid: converts to a typed Guid")]
    public void Guid_OnGuidValue_ShouldConvertToTypedGuid()
    {
        Guid id = Guid.Parse("d3f1a2b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b");
        TryMatch("/items/{id:guid}", $"/items/{id}", out RouteValueDictionary values).ShouldBeTrue();
        values["id"].ShouldBeOfType<Guid>().ShouldBe(id);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint guid: rejects non-guids")]
    public void Guid_OnNonGuid_ShouldReject()
    {
        TryMatch("/items/{id:guid}", "/items/not-a-guid", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint datetime: converts to a typed DateTime (invariant culture)")]
    public void DateTime_OnDateValue_ShouldConvertToTypedDateTime()
    {
        TryMatch("/events/{on:datetime}", "/events/2026-07-08", out RouteValueDictionary values).ShouldBeTrue();
        values["on"].ShouldBeOfType<DateTime>().ShouldBe(new DateTime(2026, 7, 8));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint datetime: rejects non-dates")]
    public void DateTime_OnNonDate_ShouldReject()
    {
        TryMatch("/events/{on:datetime}", "/events/not-a-date", out _).ShouldBeFalse();
    }

    // ---- Text / value validators (leave the value a string) -----------------------------------

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Constraint length(n): enforces an exact length")]
    [InlineData("/codes/abc", true)]
    [InlineData("/codes/ab", false)]
    [InlineData("/codes/abcd", false)]
    public void Length_Exact_ShouldEnforceLength(string path, bool expected)
    {
        TryMatch("/codes/{code:length(3)}", path, out RouteValueDictionary values).ShouldBe(expected);
        if (expected)
        {
            values["code"].ShouldBeOfType<string>(); // length validators do not convert
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Constraint length(min,max): enforces an inclusive range")]
    [InlineData("/codes/ab", true)]
    [InlineData("/codes/abcd", true)]
    [InlineData("/codes/a", false)]
    [InlineData("/codes/abcde", false)]
    public void Length_Range_ShouldEnforceBounds(string path, bool expected)
    {
        TryMatch("/codes/{code:length(2,4)}", path, out _).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Constraint minlength(n): enforces a minimum length")]
    [InlineData("/codes/abc", true)]
    [InlineData("/codes/ab", false)]
    public void MinLength_ShouldEnforceMinimum(string path, bool expected)
    {
        TryMatch("/codes/{code:minlength(3)}", path, out _).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Constraint maxlength(n): enforces a maximum length")]
    [InlineData("/codes/abc", true)]
    [InlineData("/codes/abcd", false)]
    public void MaxLength_ShouldEnforceMaximum(string path, bool expected)
    {
        TryMatch("/codes/{code:maxlength(3)}", path, out _).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Constraint min(n): enforces an inclusive lower bound")]
    [InlineData("/n/10", true)]
    [InlineData("/n/11", true)]
    [InlineData("/n/9", false)]
    public void Min_ShouldEnforceLowerBound(string path, bool expected)
    {
        TryMatch("/n/{v:min(10)}", path, out _).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Constraint max(n): enforces an inclusive upper bound")]
    [InlineData("/n/10", true)]
    [InlineData("/n/9", true)]
    [InlineData("/n/11", false)]
    public void Max_ShouldEnforceUpperBound(string path, bool expected)
    {
        TryMatch("/n/{v:max(10)}", path, out _).ShouldBe(expected);
    }

    // ---- Composition and custom conversion ----------------------------------------------------

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - Constraint composition: int + min both apply, value stays typed")]
    [InlineData("/n/5", true)]
    [InlineData("/n/0", false)]  // fails min(1)
    [InlineData("/n/abc", false)] // fails int
    public void IntWithMin_ShouldValidateBothAndKeepTypedValue(string path, bool expected)
    {
        bool matched = TryMatch("/n/{id:int:min(1)}", path, out RouteValueDictionary values);
        matched.ShouldBe(expected);
        if (expected)
        {
            values["id"].ShouldBeOfType<int>().ShouldBe(5);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Constraint custom: a custom TypedRouteParameterPolicy contributes typed conversion")]
    public void CustomTypedPolicy_ShouldContributeTypedConversion()
    {
        // Arrange — register a custom typed constraint that converts to System.Version.
        RouteParameterPolicyMap map = RouteParameterPolicyMap.CreateDefault()
            .Add("version", static _ => new VersionRouteParameterPolicy());
        Route route = new(HttpMethod.Get, "/api/{v:version}", map);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/api/1.2.3");

        // Act
        bool matched = route.TryMatch(context, out RouteValueDictionary values);

        // Assert
        matched.ShouldBeTrue();
        values["v"].ShouldBeOfType<Version>().ShouldBe(new Version(1, 2, 3));
    }

    private sealed class VersionRouteParameterPolicy : TypedRouteParameterPolicy
    {
        public override Type ConversionType => typeof(Version);

        public override bool TryConvert(string value, out object? converted)
        {
            if (Version.TryParse(value, out Version? version))
            {
                converted = version;
                return true;
            }

            converted = null;
            return false;
        }
    }
}
