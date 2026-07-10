using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Covers the three bodyless built-ins: status-only, <c>204 No Content</c>, and the do-nothing
/// empty result.
/// </summary>
public class StatusResultTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - StatusCode: sets only the status code")]
    public async Task ExecuteAsync_StatusCodeResult_SetsOnlyStatus()
    {
        // Arrange
        TestHttpContext context = new();
        IResult result = Results.StatusCode(HttpStatusCode.Accepted);

        // Act
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(202);
        context.Response.Headers.ShouldBeEmpty();
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - StatusCode: typed carrier exposes the status code")]
    public void StatusCode_TypedCarrier_ExposesStatus()
    {
        // Arrange + Act
        StatusCodeHttpResult result = TypedResults.StatusCode(HttpStatusCode.NotFound);

        // Assert
        result.StatusCode.Value.ShouldBe(404);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - NoContent: sets 204 and writes nothing")]
    public async Task ExecuteAsync_NoContentResult_Sets204AndWritesNothing()
    {
        // Arrange
        TestHttpContext context = new();

        // Act
        await Results.NoContent().ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(204);
        context.Response.Headers.ShouldBeEmpty();
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - NoContent: both factories return the shared stateless instance")]
    public void NoContent_BothFactories_ReturnSharedInstance()
    {
        // Arrange + Act + Assert
        Results.NoContent().ShouldBeSameAs(TypedResults.NoContent());
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Empty: leaves the response exactly as the pipeline shaped it")]
    public async Task ExecuteAsync_EmptyResult_LeavesResponseUntouched()
    {
        // Arrange — a middleware already shaped the response.
        TestHttpContext context = new();
        context.Response.StatusCode = HttpStatusCode.Accepted;
        context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";

        // Act
        await Results.Empty().ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(202);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("text/plain");
        context.ResponseBodyText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Empty: both factories return the shared stateless instance")]
    public void Empty_BothFactories_ReturnSharedInstance()
    {
        // Arrange + Act + Assert
        Results.Empty().ShouldBeSameAs(TypedResults.Empty());
    }
}
