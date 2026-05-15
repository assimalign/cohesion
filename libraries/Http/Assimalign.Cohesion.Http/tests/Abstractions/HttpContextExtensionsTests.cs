using Assimalign.Cohesion.Http.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpContextExtensionsTests
{
    [Fact]
    public void Deconstruct_AbstractContext_ShouldReturnTypedRequestAndResponse()
    {
        // Arrange
        TestHttpRequest request = new()
        {
            Host = "api.example.com",
            Path = "/v1/health",
            Method = HttpMethod.Get,
            Scheme = HttpScheme.Https
        };
        TestHttpResponse response = new()
        {
            StatusCode = HttpStatusCode.Accepted
        };
        TestHttpContext context = new(
            HttpVersion.Http20,
            request,
            response);

        // Act
        context.Deconstruct(out HttpVersion version, out HttpRequest actualRequest, out HttpResponse actualResponse);

        // Assert
        version.ShouldBe(HttpVersion.Http20);
        actualRequest.ShouldBeSameAs(request);
        actualResponse.ShouldBeSameAs(response);
    }

    [Fact]
    public void Deconstruct_InterfaceContext_ShouldReturnInterfaceViewsOverSameInstances()
    {
        // Arrange
        TestHttpRequest request = new();
        TestHttpResponse response = new();
        IHttpContext context = new TestHttpContext(
            HttpVersion.Http11,
            request,
            response);

        // Act
        context.Deconstruct(out HttpVersion version, out IHttpRequest actualRequest, out IHttpResponse actualResponse);

        // Assert
        version.ShouldBe(HttpVersion.Http11);
        actualRequest.ShouldBeSameAs(request);
        actualResponse.ShouldBeSameAs(response);
    }
}
