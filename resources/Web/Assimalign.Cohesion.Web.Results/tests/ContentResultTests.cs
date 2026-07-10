using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Results.Tests;

/// <summary>
/// Covers the buffered text built-ins (<c>Results.Text</c> / <c>Results.Content</c>): body,
/// default and explicit <c>Content-Type</c>, <c>Content-Length</c>, and status handling.
/// </summary>
public class ContentResultTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Results] - Text: writes UTF-8 body with the text default content type")]
    public async Task ExecuteAsync_TextResult_WritesBodyWithTextDefaults()
    {
        // Arrange
        TestHttpContext context = new();

        // Act
        await Results.Text("hello world").ExecuteAsync(context);

        // Assert
        context.ResponseBodyText().ShouldBe("hello world");
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("text/plain; charset=utf-8");
        context.Response.Headers[HttpHeaderKey.ContentLength].Value.ShouldBe("11");
        context.Response.StatusCode.Value.ShouldBe(200); // untouched default
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Content: honors an explicit content type and status code")]
    public async Task ExecuteAsync_ContentResult_HonorsContentTypeAndStatus()
    {
        // Arrange
        TestHttpContext context = new();
        IResult result = Results.Content("<p>hi</p>", "text/html; charset=utf-8", HttpStatusCode.Created);

        // Act
        await result.ExecuteAsync(context);

        // Assert
        context.ResponseBodyText().ShouldBe("<p>hi</p>");
        context.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("text/html; charset=utf-8");
        context.Response.StatusCode.Value.ShouldBe(201);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Content: Content-Length counts UTF-8 bytes, not chars")]
    public async Task ExecuteAsync_NonAsciiContent_CountsUtf8Bytes()
    {
        // Arrange — "héllo" is 5 chars but 6 UTF-8 bytes.
        TestHttpContext context = new();

        // Act
        await Results.Text("héllo").ExecuteAsync(context);

        // Assert
        context.Response.Headers[HttpHeaderKey.ContentLength].Value.ShouldBe("6");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Results] - Text/Content: a null body is rejected at the factory")]
    public void Factory_NullContent_Throws()
    {
        // Arrange + Act + Assert
        Should.Throw<ArgumentNullException>(() => Results.Text(null!));
        Should.Throw<ArgumentNullException>(() => Results.Content(null!));
        Should.Throw<ArgumentNullException>(() => TypedResults.Text(null!));
        Should.Throw<ArgumentNullException>(() => TypedResults.Content(null!));
    }
}
