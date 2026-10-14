using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Api.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Api.Tests;

/// <summary>
/// End-to-end coverage for handler return values (#1059): a value a typed handler returns — directly,
/// through <c>Task&lt;T&gt;</c>, or through <c>ValueTask&lt;T&gt;</c> — is written as the response by the
/// generated thunk, with content negotiation for serialized values, <c>text/plain</c> for strings, and
/// <c>204 No Content</c> for <see langword="null"/>.
/// </summary>
public class EndpointReturnValueTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    // A method-group handler: bound and written exactly like a lambda.
    private static Order GetOrder(long id) => new(id, "method-group");

    private static WebApplicationTestFactory CreateFactory(bool registerJson = true)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        if (registerJson)
        {
            factory.Builder.Services.AddJsonSerialization(ApiTestJsonContext.Default);
        }

        return factory;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a returned value is written as negotiated JSON with status 200")]
    public async Task MapGet_ValueReturn_ShouldWriteNegotiatedJson()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id}", (long id) => new Order(id, "widget"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/orders/7", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType.ToString().ShouldBe("application/json; charset=utf-8");
        response.Headers.Vary.ShouldContain("Accept");
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("""{"id":7,"item":"widget"}""");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a Task<T> handler's awaited value is written")]
    public async Task MapGet_TaskOfValueReturn_ShouldWriteAwaitedValue()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id}", async (long id) =>
        {
            await Task.Yield();
            return new Order(id, "task");
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/orders/8", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("""{"id":8,"item":"task"}""");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a ValueTask<T> handler's awaited value is written")]
    public async Task MapGet_ValueTaskOfValueReturn_ShouldWriteAwaitedValue()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id}", (long id) => ValueTask.FromResult(new Order(id, "value-task")));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/orders/9", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("""{"id":9,"item":"value-task"}""");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a returned string is written as UTF-8 text/plain")]
    public async Task MapGet_StringReturn_ShouldWriteTextPlain()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/greet/{name}", (string name) => $"héllo {name}");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/greet/ada", cancellation.Token);

        // Assert — text, not a JSON string, and not negotiated.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType.ToString().ShouldBe("text/plain; charset=utf-8");
        response.Headers.Vary.ShouldNotContain("Accept");
        Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(cancellation.Token)).ShouldBe("héllo ada");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a string keeps a Content-Type the handler set")]
    public async Task MapGet_StringReturnWithHandlerContentType_ShouldKeepContentType()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/fragment", (IHttpContext context) =>
        {
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/html; charset=utf-8";
            return "<p>hi</p>";
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/fragment", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType.ToString().ShouldBe("text/html; charset=utf-8");
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("<p>hi</p>");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: null answers 204 No Content with no body")]
    public async Task MapGet_NullReturn_ShouldAnswerNoContent()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id}", (long id) => id > 100 ? new Order(id, "found") : null);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/orders/1", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
        response.Content.Headers.ContentType.ShouldBeNull();
        (await response.Content.ReadAsByteArrayAsync(cancellation.Token)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: null keeps a status the handler set, with no body")]
    public async Task MapGet_NullReturnWithHandlerStatus_ShouldKeepStatus()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id}", (long id, IHttpContext context) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NotFound;
            return (Order?)null;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/orders/1", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        (await response.Content.ReadAsByteArrayAsync(cancellation.Token)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a value keeps a status the handler set")]
    public async Task MapPost_ValueReturnWithHandlerStatus_ShouldKeepStatus()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapPost("/orders", (Widget widget, IHttpContext context) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Created;
            return new Order(widget.Quantity, widget.Name);
        });

        using HttpClient client = factory.CreateClient();
        using StringContent content = new("""{"name":"gizmo","quantity":3}""", Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/orders", content, cancellation.Token);

        // Assert — the body binds, and the returned value is written under the handler's 201.
        response.StatusCode.ShouldBe(NetHttpStatusCode.Created);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("""{"id":3,"item":"gizmo"}""");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: an Accept the registry cannot satisfy answers 406")]
    public async Task MapGet_UnacceptableAccept_ShouldAnswerNotAcceptable()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id}", (long id) => new Order(id, "widget"));

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        // Act
        using HttpResponseMessage response = await client.GetAsync("/orders/7", cancellation.Token);

        // Assert — RFC 9110 §15.5.7: nothing acceptable, a bodyless 406 that still varies by Accept.
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotAcceptable);
        response.Headers.Vary.ShouldContain("Accept");
        (await response.Content.ReadAsByteArrayAsync(cancellation.Token)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a Nullable<T> result writes its value, or 204 when empty")]
    public async Task MapGet_NullableValueReturn_ShouldWriteValueOrNoContent()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/limit", (int? limit) => limit);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage present = await client.GetAsync("/limit?limit=5", cancellation.Token);
        using HttpResponseMessage absent = await client.GetAsync("/limit", cancellation.Token);

        // Assert — the value is written as its underlying int contract.
        present.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await present.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("5");
        absent.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a method-group handler binds and writes like a lambda")]
    public async Task MapGet_MethodGroupHandler_ShouldBindAndWrite()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id}", GetOrder);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/orders/11", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("""{"id":11,"item":"method-group"}""");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a route-group endpoint writes its returned value")]
    public async Task MapGroup_ValueReturn_ShouldWriteValue()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();
        factory.Application.UseRouting();
        IRouterGroupBuilder api = factory.Application.MapGroup("api/{tenant}");
        api.MapGet("orders/{id:long}", (string tenant, long id) => new Order(id, tenant));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/api/contoso/orders/4", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("""{"id":4,"item":"contoso"}""");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a type the registered resolver does not cover faults instead of writing")]
    public async Task MapGet_ReturnWithoutContract_ShouldFault()
    {
        // Arrange — the exception boundary stand-in records the fault and answers 500.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory();

        HttpContentSerializationException? fault = null;
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (HttpContentSerializationException exception)
            {
                fault = exception;
                context.Response.StatusCode = CohesionHttpStatusCode.InternalServerError;
            }
        });
        factory.Application.UseRouting();
        factory.Application.MapGet("/unregistered", () => new Unregistered("x"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/unregistered", cancellation.Token);

        // Assert — a composition fault, never a silently reflected or empty body.
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        fault.ShouldNotBeNull();
        fault.Message.ShouldContain(nameof(Unregistered), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: an application with no serialization registry faults instead of writing")]
    public async Task MapGet_ReturnWithoutRegistry_ShouldFault()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(registerJson: false);

        HttpContentSerializationException? fault = null;
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (HttpContentSerializationException exception)
            {
                fault = exception;
                context.Response.StatusCode = CohesionHttpStatusCode.InternalServerError;
            }
        });
        factory.Application.UseRouting();
        factory.Application.MapGet("/orders/{id}", (long id) => new Order(id, "widget"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/orders/7", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        fault.ShouldNotBeNull();
        fault.Message.ShouldContain("AddJsonSerialization", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Api] - Return values: a string needs no serialization registry")]
    public async Task MapGet_StringReturnWithoutRegistry_ShouldWriteText()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(registerJson: false);
        factory.Application.UseRouting();
        factory.Application.MapGet("/ping", () => "pong");

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/ping", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("pong");
    }
}
