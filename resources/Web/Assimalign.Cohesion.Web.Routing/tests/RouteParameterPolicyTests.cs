using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;
using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RouteParameterPolicyTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatch: Should resolve registered inline policies")]
    public void TryMatch_OnRegisteredInlinePolicy_ShouldUseCustomConstraint()
    {
        // Arrange
        RouteParameterPolicyMap policyMap = RouteParameterPolicyMap.CreateDefault()
            .Add("slug", static _ => new PredicateRouteParameterPolicy(static context =>
            {
                if (context.ParameterValue is not string value || value.Length == 0)
                {
                    return false;
                }

                foreach (char character in value)
                {
                    if (!char.IsLetterOrDigit(character) && character != '-')
                    {
                        return false;
                    }
                }

                return true;
            }));

        Route route = new(HttpMethod.Get, "/posts/{slug:slug}", policyMap);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/posts/hello-world");

        // Act
        bool matched = route.TryMatch(context, out RouteValueDictionary values);

        // Assert
        matched.ShouldBeTrue();
        values["slug"].ShouldBe("hello-world");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatch: Should apply conditional policies using route criteria")]
    public void TryMatch_OnConditionalPolicy_ShouldFilterRouteCandidates()
    {
        // Arrange
        Route route = new(HttpMethod.Get, "/{area}/{controller:when(area=admin)}");
        TestHttpContext adminContext = TestHttpContext.Create(HttpMethod.Get, "/admin/users");
        TestHttpContext publicContext = TestHttpContext.Create(HttpMethod.Get, "/public/users");

        // Act
        bool matchedForAdmin = route.TryMatch(adminContext, out RouteValueDictionary values);
        bool matchedForPublic = route.TryMatch(publicContext, out _);

        // Assert
        matchedForAdmin.ShouldBeTrue();
        values["area"].ShouldBe("admin");
        values["controller"].ShouldBe("users");
        matchedForPublic.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - TryMatch: Should support context-aware predicate policies")]
    public void TryMatch_OnContextAwarePolicy_ShouldUseHttpContextCriteria()
    {
        // Arrange
        RoutePattern pattern = RoutePatternFactory.Pattern(
            "/tenants/{tenant}",
            new RoutePatternPathSegment[]
            {
                RoutePatternFactory.Segment(
                    new RoutePatternSegment[]
                    {
                        RoutePatternFactory.LiteralPart("tenants"),
                    }),
                RoutePatternFactory.Segment(
                    new RoutePatternSegment[]
                    {
                        RoutePatternFactory.ParameterPart(
                            "tenant",
                            defaultValue: null,
                            RoutePatternParameterKind.Standard,
                            parameterPolicies: new[]
                            {
                                RoutePatternFactory.ParameterPolicy(
                                    new PredicateRouteParameterPolicy(static context =>
                                    {
                                        if (context.HttpContext is null ||
                                            context.ParameterValue is not string tenantValue)
                                        {
                                            return false;
                                        }

                                        return context.HttpContext.Items.TryGetValue("tenant", out object? item) &&
                                            item is string expectedTenant &&
                                            string.Equals(expectedTenant, tenantValue, StringComparison.OrdinalIgnoreCase);
                                    })),
                            })
                    }),
            });

        Route route = new(HttpMethod.Get, pattern);
        TestHttpContext allowedContext = TestHttpContext.Create(HttpMethod.Get, "/tenants/alpha");
        allowedContext.Items["tenant"] = "alpha";

        TestHttpContext rejectedContext = TestHttpContext.Create(HttpMethod.Get, "/tenants/beta");
        rejectedContext.Items["tenant"] = "alpha";

        // Act
        bool matchedAllowed = route.TryMatch(allowedContext, out _);
        bool matchedRejected = route.TryMatch(rejectedContext, out _);

        // Assert
        matchedAllowed.ShouldBeTrue();
        matchedRejected.ShouldBeFalse();
    }
}
