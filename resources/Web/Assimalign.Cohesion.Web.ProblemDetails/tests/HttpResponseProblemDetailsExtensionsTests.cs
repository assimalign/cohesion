using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Web.ProblemDetailsTests.TestObjects;

namespace Assimalign.Cohesion.Web.ProblemDetailsTests;

/// <summary>
/// Covers the imperative write path: <c>response.WriteProblemDetailsAsync(problem)</c> renders the
/// RFC 9457 payload onto the response with the status, <c>Content-Type</c>, and
/// <c>Content-Length</c> the wire expects.
/// </summary>
public class HttpResponseProblemDetailsExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.ProblemDetails] - WriteProblemDetailsAsync: renders problem+json onto the response")]
    public async Task WriteProblemDetailsAsync_WithProblem_WritesPayload()
    {
        // Arrange
        TestHttpContext context = new();
        ProblemDetails problem = ProblemDetails.FromStatus(HttpStatusCode.NotFound, "missing");

        // Act
        await context.Response.WriteProblemDetailsAsync(problem);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(404);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeTrue();

        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Not Found");
        document.RootElement.GetProperty("detail").GetString().ShouldBe("missing");
    }

    [Theory(DisplayName = "Cohesion Test [Web.ProblemDetails] - WriteProblemDetailsAsync: unregistered codes retain their status and default title")]
    [InlineData(499, "Error")]
    [InlineData(599, "Internal Server Error")]
    public async Task WriteProblemDetailsAsync_UnregisteredCode_ShouldWriteStatusAndGenericTitle(int status, string expectedTitle)
    {
        // Arrange
        TestHttpContext context = new();
        ProblemDetails problem = ProblemDetails.FromStatus(new HttpStatusCode(status));

        // Act
        await context.Response.WriteProblemDetailsAsync(problem, CancellationToken.None);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(status);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(status);
        document.RootElement.GetProperty("title").GetString().ShouldBe(expectedTitle);
    }

    [Theory(DisplayName = "Cohesion Test [Web.ProblemDetails] - WriteProblemDetailsAsync: unregistered codes preserve an application title")]
    [InlineData(499)]
    [InlineData(599)]
    public async Task WriteProblemDetailsAsync_UnregisteredCodeWithTitle_ShouldPreserveTitle(int status)
    {
        // Arrange
        TestHttpContext context = new();
        ProblemDetails problem = new() { Status = status, Title = "Application-defined failure" };

        // Act
        await context.Response.WriteProblemDetailsAsync(problem, CancellationToken.None);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(status);
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(status);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Application-defined failure");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ProblemDetails] - WriteProblemDetailsAsync: a status-less problem leaves the response status untouched")]
    public async Task WriteProblemDetailsAsync_WithoutStatus_LeavesStatusUntouched()
    {
        // Arrange
        TestHttpContext context = new();
        context.Response.StatusCode = HttpStatusCode.Accepted;
        ProblemDetails problem = new() { Detail = "context already chose the status" };

        // Act
        await context.Response.WriteProblemDetailsAsync(problem);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(202);
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("type").GetString().ShouldBe("about:blank");
        document.RootElement.TryGetProperty("status", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.ProblemDetails] - WriteProblemDetailsAsync: a null problem is rejected")]
    public async Task WriteProblemDetailsAsync_NullProblem_Throws()
    {
        // Arrange
        TestHttpContext context = new();

        // Act + Assert
        await Should.ThrowAsync<ArgumentNullException>(() => context.Response.WriteProblemDetailsAsync(null!));
    }
}
