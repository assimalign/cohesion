using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Proxy.Internal;
using Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// The forward proxy's answers that need no connection, called directly so that requests no transport
/// delivers reach them too.
/// </summary>
public class ForwardProxyMiddlewareTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Middleware: A request the forward proxy refuses is answered without a connection or the next middleware")]
    [InlineData("CONNECT", "example.com", "/", 400)]
    [InlineData("GET", "", "/", 400)]
    [InlineData("GET", "exa mple.com", "/", 400)]
    [InlineData("GET", "example.com", "/a/..%2Fb", 400)]
    [InlineData("GET", "example.com", "/a/..\\b", 400)]
    [InlineData("GET", "other.test", "/", 403)]
    [InlineData("GET", "example.com:8080", "/", 403)]
    [InlineData("CONNECT", "example.com:80", "/", 403)]
    public async Task InvokeAsync_RefusedRequest_ShouldAnswerWithoutCallingNext(string method, string host, string path, int expected)
    {
        // Arrange — the rule allows example.com on its default ports: 80 for HTTP, 443 for CONNECT.
        ForwardProxyBuilder builder = new();
        builder.Options.AddRule(new ForwardProxyRule { Hosts = ["example.com"] });
        using ForwardProxySetting setting = (ForwardProxySetting)builder.Build();
        ForwardProxyMiddleware middleware = new(setting);
        TestHttpContext context = new(HttpMethod.GetCanonicalizedValue(method), host, path);
        bool nextCalled = false;

        // Act
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // Assert
        ((int)context.Response.StatusCode).ShouldBe(expected);
        nextCalled.ShouldBeFalse();
    }
}
