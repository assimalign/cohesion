using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Api.Tests;

/// <summary>
/// End-to-end coverage for the endpoint convention builders (#1055): raw and source-generated
/// <c>Map*</c> return the route's builder, groups hold typed endpoints, and metadata attached through
/// either reaches middleware registered after <c>UseRouting</c>.
/// </summary>
public class EndpointConventionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private sealed class TagMetadata
    {
        public TagMetadata(string tag) => Tag = tag;

        public string Tag { get; }
    }

    private static async Task WriteTextAsync(IHttpContext context, string text)
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), context.RequestCancelled);
    }

    // Echoes the endpoint's TagMetadata in a response header, read between routing and the endpoint.
    private static void UseTagEcho(WebApplicationTestFactory factory)
    {
        factory.Application.Use(async (context, next) =>
        {
            if (context.GetEndpointMetadata<TagMetadata>() is { } tag)
            {
                context.Response.Headers[new HttpHeaderKey("X-Tag")] = tag.Tag;
            }

            await next.Invoke(context);
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Conventions: Metadata on a typed endpoint reaches middleware after UseRouting")]
    public async Task WithMetadata_OnTypedEndpoint_ShouldReachLaterMiddleware()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Application.UseRouting();
        UseTagEcho(factory);

        factory.Application
            .MapGet("/widgets/{id}", async (int id, IHttpContext context) => await WriteTextAsync(context, $"widget:{id}"))
            .WithMetadata(new TagMetadata("typed"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/widgets/7", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Headers.GetValues("X-Tag").ShouldBe(new[] { "typed" });
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("widget:7");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Conventions: A group holds typed endpoints and applies its metadata to them")]
    public async Task MapGroup_WithTypedEndpoint_ShouldBindAndApplyGroupMetadata()
    {
        // Arrange — the group's metadata is attached after its endpoint is mapped.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Application.UseRouting();
        UseTagEcho(factory);

        IRouterGroupBuilder api = factory.Application.MapGroup("api/{tenant}");
        api.MapGet("orders/{id:int}", async (string tenant, int id, IHttpContext context) =>
            await WriteTextAsync(context, $"{tenant}:{id}"));
        api.WithMetadata(new TagMetadata("group"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/api/contoso/orders/5", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Headers.GetValues("X-Tag").ShouldBe(new[] { "group" });
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("contoso:5");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Conventions: WithName on a raw endpoint drives link generation")]
    public async Task WithName_OnRawEndpoint_ShouldGenerateLinks()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();
        factory.Application.UseRouting();

        factory.Application.MapGet("/users/{id:int}", context => WriteTextAsync(context, "user")).WithName("user");
        factory.Application.MapGet("/link", context =>
            WriteTextAsync(context, context.GetLinkGenerator().GetPathByName("user", new RouteValueDictionary { ["id"] = 3 })));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/link", cancellation.Token);

        // Assert
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("/users/3");
    }
}
