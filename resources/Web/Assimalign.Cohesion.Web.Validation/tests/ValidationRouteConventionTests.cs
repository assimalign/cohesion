using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Validation.Tests;

/// <summary>
/// The convention verbs and their metadata: <c>RequireValidation()</c> and <c>DisableValidation()</c> append
/// the shared <see cref="ValidationMetadata"/> instances, and the most specific declaration wins.
/// </summary>
public class ValidationRouteConventionTests
{
    private static IRouterRoute GetRoute(WebApplicationTestFactory factory, string routeName)
    {
        IRouter router = factory.Application.Context.Features.OfType<IRouterFeature>().Single().Router;

        return router.Routes.Single(route => route.Metadata.GetMetadata<RouteNameMetadata>()?.RouteName == routeName);
    }

    private static Task WriteNothingAsync(IHttpContext context) => Task.CompletedTask;

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Metadata: the shared instances carry their state")]
    public void ValidationMetadata_SharedInstances_ShouldCarryTheirState()
    {
        // Assert
        ValidationMetadata.Required.RequiresValidation.ShouldBeTrue();
        ValidationMetadata.Disabled.RequiresValidation.ShouldBeFalse();
        ValidationMetadata.Required.ShouldBeSameAs(ValidationMetadata.Required);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Conventions: the verbs append the shared metadata and the route's declaration wins over its group's")]
    public async Task Verbs_RouteAndGroup_ShouldResolveTheMostSpecificDeclaration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Application.UseRouting();

        IRouterGroupBuilder group = factory.Application.MapGroup("api").DisableValidation();
        group.MapPost("inherits", WriteNothingAsync).WithName("inherits");
        group.MapPost("overrides", WriteNothingAsync).WithName("overrides").RequireValidation();
        factory.Application.MapPost("/plain", WriteNothingAsync).WithName("plain");

        // Act — the first request builds the route table.
        using System.Net.Http.HttpClient client = factory.CreateClient();
        using System.Net.Http.HttpResponseMessage response = await client.PostAsync("/plain", null);
        IReadOnlyList<ValidationMetadata> inherits = GetRoute(factory, "inherits").Metadata.GetOrderedMetadata<ValidationMetadata>();
        ValidationMetadata? overrides = GetRoute(factory, "overrides").Metadata.GetMetadata<ValidationMetadata>();
        ValidationMetadata? plain = GetRoute(factory, "plain").Metadata.GetMetadata<ValidationMetadata>();

        // Assert
        inherits.ShouldHaveSingleItem().ShouldBeSameAs(ValidationMetadata.Disabled);
        overrides.ShouldBeSameAs(ValidationMetadata.Required);
        plain.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Conventions: the verbs return the builder they were called on")]
    public async Task Verbs_RouteBuilder_ShouldReturnTheSameBuilder()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        IRouterRouteBuilder route = factory.Application.MapPost("/chained", WriteNothingAsync);

        // Act
        IRouterRouteBuilder required = route.RequireValidation();
        IRouterRouteBuilder disabled = route.DisableValidation();

        // Assert
        required.ShouldBeSameAs(route);
        disabled.ShouldBeSameAs(route);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Conventions: a null builder is rejected")]
    public void Verbs_NullBuilder_ShouldThrow()
    {
        // Arrange
        IRouterRouteBuilder? route = null;

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => route!.RequireValidation());
        Should.Throw<ArgumentNullException>(() => route!.DisableValidation());
    }
}
