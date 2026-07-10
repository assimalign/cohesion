using System;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Covers the RFC 9457 problem built-in: normalization defaults (status 500, status-phrase title
/// for the reserved type), member pass-through, extension rendering, and the
/// <c>application/problem+json</c> response shape via the single AOT-safe writer.
/// </summary>
public class ProblemResultTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - Problem: a bare Problem() defaults to a 500 about:blank payload")]
    public async Task ExecuteAsync_BareProblem_DefaultsTo500()
    {
        // Arrange
        TestHttpContext context = new();

        // Act
        await Results.Problem().ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(500);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");
        context.Response.Headers.ContainsKey(HttpHeaderKey.ContentLength).ShouldBeTrue();

        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("type").GetString().ShouldBe("about:blank");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(500);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Internal Server Error");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Problem: individual members flow into the payload with a status-phrase title")]
    public async Task ExecuteAsync_MemberOverloads_FlowIntoPayload()
    {
        // Arrange
        TestHttpContext context = new();
        IResult result = Results.Problem(
            detail: "The widget is missing.",
            instance: "/widgets/42",
            statusCode: HttpStatusCode.NotFound);

        // Act
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(404);
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Not Found");
        document.RootElement.GetProperty("detail").GetString().ShouldBe("The widget is missing.");
        document.RootElement.GetProperty("instance").GetString().ShouldBe("/widgets/42");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Problem: a custom type keeps its own title and renders extensions")]
    public async Task ExecuteAsync_CustomProblemDetails_KeepsTitleAndExtensions()
    {
        // Arrange
        TestHttpContext context = new();
        ProblemDetails problem = new()
        {
            Type = "https://example.com/probs/out-of-credit",
            Title = "You do not have enough credit.",
            Status = 403,
        };
        problem.Extensions["balance"] = 30;

        // Act
        await TypedResults.Problem(problem).ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(403);
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("type").GetString().ShouldBe("https://example.com/probs/out-of-credit");
        document.RootElement.GetProperty("title").GetString().ShouldBe("You do not have enough credit.");
        document.RootElement.GetProperty("balance").GetInt32().ShouldBe(30);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Problem: a custom type without a title is not given the status phrase")]
    public async Task ExecuteAsync_CustomTypeWithoutTitle_LeavesTitleAbsent()
    {
        // Arrange — the status-phrase title default applies only to the reserved about:blank type
        // (RFC 9457 §4.2); a custom problem type defines its own vocabulary.
        TestHttpContext context = new();
        ProblemDetails problem = new()
        {
            Type = "https://example.com/probs/quota",
            Status = 429,
        };

        // Act
        await Results.Problem(problem).ExecuteAsync(context);

        // Assert
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.TryGetProperty("title", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Problem: the typed carrier exposes the normalized payload")]
    public void Problem_TypedCarrier_ExposesNormalizedPayload()
    {
        // Arrange + Act
        ProblemHttpResult result = TypedResults.Problem(new ProblemDetails());

        // Assert
        result.ProblemDetails.Status.ShouldBe(500);
        result.ProblemDetails.Title.ShouldBe("Internal Server Error");
        result.ContentType.ShouldBe("application/problem+json");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Problem: a null payload is rejected at the factory")]
    public void Factory_NullProblemDetails_Throws()
    {
        // Arrange + Act + Assert
        Should.Throw<ArgumentNullException>(() => Results.Problem((ProblemDetails)null!));
        Should.Throw<ArgumentNullException>(() => TypedResults.Problem((ProblemDetails)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - WriteProblemDetailsAsync: renders problem+json onto the response imperatively")]
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
        using JsonDocument document = JsonDocument.Parse(context.ResponseBodyText());
        document.RootElement.GetProperty("detail").GetString().ShouldBe("missing");
    }
}
