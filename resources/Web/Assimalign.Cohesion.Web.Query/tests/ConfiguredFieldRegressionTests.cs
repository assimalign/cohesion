using System;

using Assimalign.Cohesion.Http;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Query.Tests;

public sealed class ConfiguredFieldRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Query] - Redirect location: rejects every control before changing the response")]
    [InlineData(false)]
    [InlineData(true)]
    public void Redirect_ControlCharacters_ShouldRejectBeforeResponseMutation(bool toGet)
    {
        // Arrange
        var context = new TestHttpContext("/search", HttpMethod.Query);
        context.Response.StatusCode = HttpStatusCode.Ok;

        // Act and assert
        for (int code = 0; code <= char.MaxValue; code++)
        {
            if (char.IsControl((char)code))
            {
                string location = $"/result{(char)code}42";
                Should.Throw<ArgumentException>(() =>
                {
                    if (toGet)
                    {
                        context.Response.RedirectQueryToGet(location);
                    }
                    else
                    {
                        context.Response.RedirectQuery(location);
                    }
                });
                context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
                context.Response.Headers.GetValue(HttpHeaderKey.Location).ShouldBeNull();
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Query] - Redirect location: rejects spaces before changing the response")]
    [InlineData("/result 42")]
    [InlineData(" /result42")]
    [InlineData("/result42 ")]
    public void Redirect_Space_ShouldRejectBeforeResponseMutation(string location)
    {
        // Arrange
        var context = new TestHttpContext("/search", HttpMethod.Query);
        context.Response.StatusCode = HttpStatusCode.Ok;

        // Act and assert
        Should.Throw<ArgumentException>(() => context.Response.RedirectQuery(location));
        Should.Throw<ArgumentException>(() => context.Response.RedirectQueryToGet(location));
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.Response.Headers.GetValue(HttpHeaderKey.Location).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Query] - Redirect location: refuses default before changing the response")]
    public void Redirect_Default_ShouldRejectBeforeResponseMutation()
    {
        // Arrange
        var context = new TestHttpContext("/search", HttpMethod.Query);
        context.Response.StatusCode = HttpStatusCode.Ok;

        // Act and assert
        Should.Throw<ArgumentException>(() => context.Response.RedirectQuery(default!));
        Should.Throw<ArgumentException>(() => context.Response.RedirectQueryToGet(default!));
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.Response.Headers.GetValue(HttpHeaderKey.Location).ShouldBeNull();
    }
}
