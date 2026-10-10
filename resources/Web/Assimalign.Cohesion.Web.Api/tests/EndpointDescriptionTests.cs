using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Api.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Api.Tests;

/// <summary>
/// End-to-end coverage for the endpoint description metadata (#152 groundwork): the source generator
/// attaches an <see cref="EndpointParameterMetadata"/> per request-bound parameter and the
/// <see cref="EndpointResponseMetadata"/> responses to every typed endpoint, readable from the built route
/// table and from the matched endpoint during a request.
/// </summary>
public class EndpointDescriptionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    // A method-group handler whose declared result admits null.
    private static Task<Order?> FindOrder(long id) => Task.FromResult<Order?>(null);

    private static WebApplicationTestFactory CreateFactory()
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddJsonSerialization(ApiTestJsonContext.Default);
        return factory;
    }

    // The route table the application built at startup: what a documentation adapter enumerates.
    private static IRouterRoute GetRoute(WebApplicationTestFactory factory, string routeName)
    {
        IRouter router = factory.Application.Context.Features.OfType<IRouterFeature>().Single().Router;

        return router.Routes.Single(route => route.Metadata.GetMetadata<RouteNameMetadata>()?.RouteName == routeName);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description: the route table describes a typed endpoint's parameters and response")]
    public async Task MapGet_TypedEndpoint_ShouldDescribeParametersAndResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application
            .MapGet("/orders/{id}", (long id, [FromQuery(Name = "q")] string? filter, [FromHeader(Name = "X-Tenant")] string tenant, IHttpContext context, CancellationToken token) =>
                new Order(id, tenant))
            .WithName("order");

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", "contoso");
        using HttpResponseMessage response = await client.GetAsync("/orders/3", cancellation.Token);

        // Act
        IRouterRoute route = GetRoute(factory, "order");
        IReadOnlyList<EndpointParameterMetadata> parameters = route.Metadata.GetOrderedMetadata<EndpointParameterMetadata>();
        IReadOnlyList<EndpointResponseMetadata> responses = route.Metadata.GetOrderedMetadata<EndpointResponseMetadata>();

        // Assert — the injected context and token are not request inputs; the order is never null.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);

        parameters.Count.ShouldBe(3);
        parameters[0].Name.ShouldBe("id");
        parameters[0].Source.ShouldBe(EndpointParameterSource.Route);
        parameters[0].Type.ShouldBe(typeof(long));
        parameters[0].IsRequired.ShouldBeTrue();
        parameters[1].Name.ShouldBe("q");
        parameters[1].Source.ShouldBe(EndpointParameterSource.Query);
        parameters[1].Type.ShouldBe(typeof(string));
        parameters[1].IsRequired.ShouldBeFalse();
        parameters[2].Name.ShouldBe("X-Tenant");
        parameters[2].Source.ShouldBe(EndpointParameterSource.Header);
        parameters[2].IsRequired.ShouldBeTrue();

        EndpointResponseMetadata ok = responses.ShouldHaveSingleItem();
        ok.StatusCode.ShouldBe(CohesionHttpStatusCode.Ok);
        ok.Type.ShouldBe(typeof(Order));
        ok.ContentType.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description: the matched endpoint's description is readable during a request")]
    public async Task MapPost_BodyEndpoint_ShouldDescribeBodyDuringRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        IReadOnlyList<EndpointParameterMetadata>? parameters = null;
        IReadOnlyList<EndpointResponseMetadata>? responses = null;
        factory.Application.UseRouting();
        factory.Application.Use(async (context, next) =>
        {
            parameters = context.GetEndpointMetadata().GetOrderedMetadata<EndpointParameterMetadata>();
            responses = context.GetEndpointMetadata().GetOrderedMetadata<EndpointResponseMetadata>();
            await next.Invoke(context);
        });
        factory.Application.MapPost("/orders", (Widget widget) => new Order(widget.Quantity, widget.Name));

        using HttpClient client = factory.CreateClient();
        using StringContent content = new("""{"name":"gizmo","quantity":3}""", Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/orders", content, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        EndpointParameterMetadata body = parameters.ShouldNotBeNull().ShouldHaveSingleItem();
        body.Name.ShouldBe("widget");
        body.Source.ShouldBe(EndpointParameterSource.Body);
        body.Type.ShouldBe(typeof(Widget));
        body.IsRequired.ShouldBeTrue();
        responses.ShouldNotBeNull().ShouldHaveSingleItem().Type.ShouldBe(typeof(Order));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description: a string result is described as text/plain and a void handler without a type")]
    public async Task MapGet_TextAndVoidEndpoints_ShouldDescribeTheirResponses()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/ping", () => "pong").WithName("text");
        factory.Application.MapGet("/raw/{id}", async (int id, IHttpContext context) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(id.ToString()), context.RequestCancelled);
        }).WithName("raw");

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/ping", cancellation.Token);

        // Act
        EndpointResponseMetadata text = GetRoute(factory, "text").Metadata.GetOrderedMetadata<EndpointResponseMetadata>().ShouldHaveSingleItem();
        EndpointResponseMetadata raw = GetRoute(factory, "raw").Metadata.GetOrderedMetadata<EndpointResponseMetadata>().ShouldHaveSingleItem();

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        text.Type.ShouldBe(typeof(string));
        text.ContentType.ShouldBe(HttpMediaType.TextPlain);
        raw.StatusCode.ShouldBe(CohesionHttpStatusCode.Ok);
        raw.Type.ShouldBeNull();
        raw.ContentType.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description: a result that may be null lists 204 after the 200")]
    public async Task MapGet_NullableResult_ShouldDescribeNoContent()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id}", FindOrder).WithName("find");

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/orders/1", cancellation.Token);

        // Act
        IReadOnlyList<EndpointResponseMetadata> responses = GetRoute(factory, "find").Metadata.GetOrderedMetadata<EndpointResponseMetadata>();

        // Assert — the description matches what the endpoint answered.
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        responses.Count.ShouldBe(2);
        responses[0].StatusCode.ShouldBe(CohesionHttpStatusCode.Ok);
        responses[0].Type.ShouldBe(typeof(Order));
        responses[1].StatusCode.ShouldBe(CohesionHttpStatusCode.NoContent);
        responses[1].Type.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description: a group endpoint describes a prefix parameter as route-or-query and composes group responses")]
    public async Task MapGroup_TypedEndpoint_ShouldDescribeRouteOrQueryAndComposeResponses()
    {
        // Arrange — the group documents a 404 every child may answer; the child adds its own.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        IRouterGroupBuilder api = factory.Application.MapGroup("api/{tenant}");
        api.MapGet("orders/{id:long}", (string tenant, long id) => new Order(id, tenant))
            .WithName("tenant-order")
            .WithMetadata(new EndpointResponseMetadata(CohesionHttpStatusCode.Conflict));
        api.WithMetadata(new EndpointResponseMetadata(CohesionHttpStatusCode.NotFound));

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/contoso/orders/2", cancellation.Token);

        // Act
        IRouterRoute route = GetRoute(factory, "tenant-order");
        IReadOnlyList<EndpointParameterMetadata> parameters = route.Metadata.GetOrderedMetadata<EndpointParameterMetadata>();
        IReadOnlyList<EndpointResponseMetadata> responses = route.Metadata.GetOrderedMetadata<EndpointResponseMetadata>();

        // Assert — 'tenant' comes from a prefix the call site cannot see; group items come first.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        parameters.Select(parameter => (parameter.Name, parameter.Source)).ShouldBe(new[]
        {
            ("tenant", EndpointParameterSource.RouteOrQuery),
            ("id", EndpointParameterSource.Route)
        });
        route.Pattern.ShouldNotBeNull().RawText.ShouldNotBeNull().ShouldContain("{tenant}", Case.Sensitive);
        responses.Select(item => item.StatusCode.Value).ShouldBe(new[] { 404, 200, 409 });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Description: form fields are described with their field names")]
    public async Task MapPost_FormEndpoint_ShouldDescribeFormFields()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapPost("/upload", ([FromForm] string title, [FromForm(Name = "qty")] int? quantity) => $"{title}:{quantity}")
            .WithName("upload");

        using HttpClient client = factory.CreateClient();
        using FormUrlEncodedContent content = new(new[] { new KeyValuePair<string, string>("title", "boxes") });
        using HttpResponseMessage response = await client.PostAsync("/upload", content, cancellation.Token);

        // Act
        IReadOnlyList<EndpointParameterMetadata> parameters = GetRoute(factory, "upload").Metadata.GetOrderedMetadata<EndpointParameterMetadata>();

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        parameters.Count.ShouldBe(2);
        parameters[0].Source.ShouldBe(EndpointParameterSource.Form);
        parameters[0].IsRequired.ShouldBeTrue();
        parameters[1].Name.ShouldBe("qty");
        parameters[1].Type.ShouldBe(typeof(int?));
        parameters[1].IsRequired.ShouldBeFalse();
    }
}
