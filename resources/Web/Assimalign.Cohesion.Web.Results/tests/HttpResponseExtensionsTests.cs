using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Verifies the <c>WriteProblemDetailsAsync</c> response extension: it sets the status code from the
/// model, sets the problem+json content headers, and writes the serialized body.
/// </summary>
public class HttpResponseExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - WriteProblemDetailsAsync: sets status, headers and body")]
    public async Task WriteProblemDetailsAsync_SetsStatusHeadersAndBody()
    {
        TestHttpContext context = new();
        ProblemDetails problem = ProblemDetails.FromStatus(HttpStatusCode.NotFound, "no such order");

        await context.Response.WriteProblemDetailsAsync(problem);

        context.Response.StatusCode.Value.ShouldBe(404);
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/problem+json");

        string body = context.ResponseBodyText();
        int contentLength = int.Parse(context.Response.Headers[HttpHeaderKey.ContentLength].Value, CultureInfo.InvariantCulture);
        contentLength.ShouldBe(System.Text.Encoding.UTF8.GetByteCount(body));

        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
        document.RootElement.GetProperty("detail").GetString().ShouldBe("no such order");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - WriteProblemDetailsAsync: leaves status unchanged when the model has none")]
    public async Task WriteProblemDetailsAsync_WithoutStatus_LeavesResponseStatus()
    {
        TestHttpContext context = new();
        context.Response.StatusCode = HttpStatusCode.Accepted;
        var problem = new ProblemDetails { Title = "no status member" };

        await context.Response.WriteProblemDetailsAsync(problem);

        context.Response.StatusCode.Value.ShouldBe(202);
    }
}
