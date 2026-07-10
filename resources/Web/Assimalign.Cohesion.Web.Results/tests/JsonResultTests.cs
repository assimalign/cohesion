using System;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Covers the source-generated JSON built-ins (<c>Json&lt;T&gt;</c> and the JSON-only
/// <c>Ok&lt;T&gt;</c>) and the <c>WriteJsonAsync&lt;T&gt;</c> response extension. All serialization
/// flows through <c>JsonTypeInfo&lt;T&gt;</c> — the tests supply the same source-generated context
/// an endpoint author would, so the suite itself stays reflection-free.
/// </summary>
public class JsonResultTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - Json: serializes the DTO through the supplied JsonTypeInfo")]
    public async Task ExecuteAsync_JsonResult_SerializesThroughTypeInfo()
    {
        // Arrange
        TestHttpContext context = new();
        IResult result = Results.Json(new TestPayload("widget", 3), TestJsonContext.Default.TestPayload);

        // Act
        await result.ExecuteAsync(context);

        // Assert
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("Name").GetString().ShouldBe("widget");
        document.RootElement.GetProperty("Count").GetInt32().ShouldBe(3);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/json; charset=utf-8");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Json: honors an explicit status code and content type")]
    public async Task ExecuteAsync_JsonResultWithStatus_SetsStatus()
    {
        // Arrange
        TestHttpContext context = new();
        IResult result = Results.Json(
            new TestPayload("widget", 3),
            TestJsonContext.Default.TestPayload,
            contentType: "application/vnd.example+json",
            statusCode: HttpStatusCode.Created);

        // Act
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(201);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/vnd.example+json");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Json: a null reference serializes as JSON null")]
    public async Task ExecuteAsync_NullValue_WritesJsonNull()
    {
        // Arrange
        TestHttpContext context = new();
        IResult result = Results.Json<TestPayload>(null, TestJsonContext.Default.TestPayload);

        // Act
        await result.ExecuteAsync(context);

        // Assert
        context.ResponseBodyText().ShouldBe("null");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Ok: sets 200 and serializes the DTO as JSON")]
    public async Task ExecuteAsync_OkResult_Sets200AndSerializes()
    {
        // Arrange
        TestHttpContext context = new();
        context.Response.StatusCode = HttpStatusCode.NotFound; // Ok must overwrite whatever is there.
        OkHttpResult<TestPayload> result = TypedResults.Ok(new TestPayload("gadget", 7), TestJsonContext.Default.TestPayload);

        // Act
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(200);
        result.StatusCode.Value.ShouldBe(200);
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("Name").GetString().ShouldBe("gadget");
        document.RootElement.GetProperty("Count").GetInt32().ShouldBe(7);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/json; charset=utf-8");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Json/Ok: missing serialization metadata is rejected at the factory")]
    public void Factory_NullTypeInfo_Throws()
    {
        // Arrange + Act + Assert
        Should.Throw<ArgumentNullException>(() => Results.Json(new TestPayload("x", 1), null!));
        Should.Throw<ArgumentNullException>(() => Results.Ok(new TestPayload("x", 1), null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - WriteJsonAsync: writes the payload and headers, leaves the status")]
    public async Task WriteJsonAsync_WithPayload_WritesBodyAndHeaders()
    {
        // Arrange
        TestHttpContext context = new();
        context.Response.StatusCode = HttpStatusCode.Accepted;

        // Act
        await context.Response.WriteJsonAsync(new TestPayload("widget", 3), TestJsonContext.Default.TestPayload);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(202); // untouched
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/json; charset=utf-8");
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("Name").GetString().ShouldBe("widget");
    }
}
