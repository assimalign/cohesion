using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// End-to-end: a real Web.Api route handler (<c>MapGet</c>) that <em>returns</em> an
/// <see cref="IResult"/> is registered through the real <c>AddRouting</c>/<c>UseRouting</c> chain
/// and executed by the pipeline against a request context — the deferred-response shape that
/// endpoints (#151) and source-generated binding (#796) build on.
/// </summary>
public class ResultPipelineIntegrationTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - Pipeline: a MapGet handler returning Ok<T> is executed end-to-end")]
    public async Task Pipeline_MapGetHandlerReturningOk_WritesJsonResponse()
    {
        // Arrange — the handler produces a result and hands it to the execution glue, mirroring
        // how endpoint bindings will consume IResult.
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting();
        app.MapGet("/widgets/{id:int}", context =>
        {
            IResult result = Results.Ok(new TestPayload("widget", 42), TestJsonContext.Default.TestPayload);
            return context.ExecuteResultAsync(result);
        });

        TestHttpContext context = new(HttpMethod.Get, "/widgets/42");

        // Act
        await app.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(200);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/json; charset=utf-8");

        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("Name").GetString().ShouldBe("widget");
        document.RootElement.GetProperty("Count").GetInt32().ShouldBe(42);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Pipeline: a handler returning Problem writes problem+json end-to-end")]
    public async Task Pipeline_MapGetHandlerReturningProblem_WritesProblemJson()
    {
        // Arrange
        TestWebApplication app = new();
        app.AddRouting();
        app.UseRouting();
        app.MapGet("/broken", context =>
            context.ExecuteResultAsync(Results.Problem(detail: "It broke.", statusCode: HttpStatusCode.ServiceUnavailable)));

        TestHttpContext context = new(HttpMethod.Get, "/broken");

        // Act
        await app.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(503);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");

        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(503);
        document.RootElement.GetProperty("detail").GetString().ShouldBe("It broke.");
    }
}
