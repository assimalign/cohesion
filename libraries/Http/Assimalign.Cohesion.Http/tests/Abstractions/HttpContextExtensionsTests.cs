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
        TestHttpContext context = new(HttpVersion.Http20);
        context.Request.Host = "api.example.com";
        context.Request.Path = "/v1/health";
        context.Request.Method = HttpMethod.Get;
        context.Request.Scheme = HttpScheme.Https;
        context.Response.StatusCode = HttpStatusCode.Accepted;

        // Act
        context.Deconstruct(out HttpVersion version, out HttpRequest actualRequest, out HttpResponse actualResponse);

        // Assert
        version.ShouldBe(HttpVersion.Http20);
        actualRequest.ShouldBeSameAs(context.Request);
        actualResponse.ShouldBeSameAs(context.Response);
    }

    [Fact]
    public void Deconstruct_InterfaceContext_ShouldReturnInterfaceViewsOverSameInstances()
    {
        // Arrange
        TestHttpContext concrete = new(HttpVersion.Http11);
        IHttpContext context = concrete;

        // Act
        context.Deconstruct(out HttpVersion version, out IHttpRequest actualRequest, out IHttpResponse actualResponse);

        // Assert
        version.ShouldBe(HttpVersion.Http11);
        actualRequest.ShouldBeSameAs(concrete.Request);
        actualResponse.ShouldBeSameAs(concrete.Response);
    }
}
