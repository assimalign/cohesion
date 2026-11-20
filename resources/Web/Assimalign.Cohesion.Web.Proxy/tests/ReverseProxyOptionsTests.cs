using System;
using System.Net;
using System.Net.Http;

using Assimalign.Cohesion.Http;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// Route validation where routes are registered, and the options' defaults.
/// </summary>
public class ReverseProxyOptionsTests
{
    private static readonly Uri _destination = new("http://backend/");

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: Routes are listed in the order they were added")]
    public void AddRoute_ValidRoutes_ShouldListThemInOrder()
    {
        // Arrange
        ReverseProxyOptions options = new();

        // Act
        options
            .AddRoute(new ReverseProxyRoute { Name = "api", PathPrefix = "/api", Destinations = [_destination] })
            .AddRoute(new ReverseProxyRoute { Name = "web", Destinations = [_destination] });

        // Assert
        options.Routes.Count.ShouldBe(2);
        options.Routes[0].Name.ShouldBe("api");
        options.Routes[1].Name.ShouldBe("web");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A null route is refused")]
    public void AddRoute_Null_ShouldThrowArgumentNullException()
    {
        // Arrange
        ReverseProxyOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.AddRoute(null!)).ParamName.ShouldBe("route");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Options: A route with no name is refused with its index")]
    [InlineData("")]
    [InlineData("   ")]
    public void AddRoute_NoName_ShouldThrowArgumentException(string name)
    {
        // Arrange
        ReverseProxyOptions options = new();
        options.AddRoute(new ReverseProxyRoute { Name = "first", Destinations = [_destination] });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => options.AddRoute(new ReverseProxyRoute { Name = name, Destinations = [_destination] }));

        // Assert
        exception.ParamName.ShouldBe("route");
        exception.Message.ShouldContain("index 1", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Options: A host pattern that is not valid is refused with the route's name")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("example.com:8080")]
    [InlineData("api.*.example.com")]
    [InlineData("*example.com")]
    public void AddRoute_InvalidHostPattern_ShouldThrowArgumentException(string pattern)
    {
        // Arrange
        ReverseProxyOptions options = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => options.AddRoute(new ReverseProxyRoute { Name = "api", Hosts = [pattern], Destinations = [_destination] }));

        // Assert
        exception.ParamName.ShouldBe("route");
        exception.Message.ShouldContain("'api'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A null host list is refused")]
    public void AddRoute_NullHosts_ShouldThrowArgumentException()
    {
        // Arrange
        ReverseProxyOptions options = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => options.AddRoute(new ReverseProxyRoute { Name = "api", Hosts = null!, Destinations = [_destination] }));

        // Assert
        exception.Message.ShouldContain("null host list", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Options: A relative path prefix or one with a query or fragment never reaches a route")]
    [InlineData("api")]
    [InlineData("/api?x=1")]
    [InlineData("/api#top")]
    public void ReverseProxyRoute_PathPrefixRefusedByHttpPath_ShouldThrowHttpException(string prefix)
    {
        // Act / Assert — the path value object refuses these when the string is converted.
        Should.Throw<HttpException>(() => new ReverseProxyRoute { Name = "api", PathPrefix = prefix, Destinations = [_destination] });
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Options: A path prefix that is not a plain absolute path is refused")]
    [InlineData("*")]
    [InlineData("*/api")]
    [InlineData("/api/../admin")]
    [InlineData("/./api")]
    [InlineData("/api/.")]
    public void AddRoute_InvalidPathPrefix_ShouldThrowArgumentException(string prefix)
    {
        // Arrange
        ReverseProxyOptions options = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => options.AddRoute(new ReverseProxyRoute { Name = "api", PathPrefix = prefix, Destinations = [_destination] }));

        // Assert
        exception.ParamName.ShouldBe("route");
        exception.Message.ShouldContain($"'{prefix}'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A route with no destination is refused")]
    public void AddRoute_NoDestination_ShouldThrowArgumentException()
    {
        // Arrange
        ReverseProxyOptions options = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => options.AddRoute(new ReverseProxyRoute { Name = "api", Destinations = [] }));

        // Assert
        exception.Message.ShouldContain("no destination", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Options: A destination that is not a bare http or https URI is refused")]
    [InlineData("ftp://backend/")]
    [InlineData("http://backend/?x=1")]
    [InlineData("http://backend/#top")]
    [InlineData("http://user:secret@backend/")]
    [InlineData("/relative")]
    public void AddRoute_InvalidDestination_ShouldThrowArgumentException(string destination)
    {
        // Arrange
        ReverseProxyOptions options = new();
        Uri uri = new(destination, UriKind.RelativeOrAbsolute);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => options.AddRoute(new ReverseProxyRoute { Name = "api", Destinations = [_destination, uri] }));

        // Assert
        exception.ParamName.ShouldBe("route");
        exception.Message.ShouldContain("'api'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A second route with a name already added is refused")]
    public void AddRoute_DuplicateName_ShouldThrowInvalidOperationException()
    {
        // Arrange
        ReverseProxyOptions options = new();
        options.AddRoute(new ReverseProxyRoute { Name = "api", Destinations = [_destination] });

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => options.AddRoute(new ReverseProxyRoute { Name = "api", PathPrefix = "/v2", Destinations = [_destination] }));

        // Assert — names compare ordinally, so 'API' would have been a different route.
        exception.Message.ShouldContain("'api'", Case.Sensitive);
        options.Routes.Count.ShouldBe(1);
        options.AddRoute(new ReverseProxyRoute { Name = "API", Destinations = [_destination] }).Routes.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: The forwarder defaults set the X-Forwarded headers and keep the destination's host")]
    public void Constructor_Defaults_ShouldForwardLikeAGateway()
    {
        // Act
        ReverseProxyOptions options = new();

        // Assert
        options.Routes.ShouldBeEmpty();
        options.Forwarder.ForwardedHeaderAction.ShouldBe(HttpForwardedHeaderAction.Set);
        options.Forwarder.PreserveHost.ShouldBeFalse();
        options.Forwarder.AddViaToResponses.ShouldBeFalse();
        options.ConfigureHandler.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Options: A handler that would change what the proxy relays fails the build")]
    [InlineData(nameof(SocketsHttpHandler.AllowAutoRedirect))]
    [InlineData(nameof(SocketsHttpHandler.UseCookies))]
    [InlineData(nameof(SocketsHttpHandler.AutomaticDecompression))]
    public void Build_HandlerChangesRelayedResponses_ShouldThrowInvalidOperationException(string property)
    {
        // Arrange
        ReverseProxyBuilder builder = new();
        builder.Options.AddRoute(new ReverseProxyRoute { Name = "api", Destinations = [_destination] });
        builder.Options.ConfigureHandler = handler =>
        {
            switch (property)
            {
                case nameof(SocketsHttpHandler.AllowAutoRedirect):
                    handler.AllowAutoRedirect = true;
                    break;
                case nameof(SocketsHttpHandler.UseCookies):
                    handler.UseCookies = true;
                    break;
                default:
                    handler.AutomaticDecompression = DecompressionMethods.GZip;
                    break;
            }
        };

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain(property, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Options: A handler callback that only tunes the pool is applied")]
    public void Build_HandlerTunesThePool_ShouldApplyTheCallback()
    {
        // Arrange
        ReverseProxyBuilder builder = new();
        TimeSpan? seen = null;
        builder.Options.ConfigureHandler = handler =>
        {
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
            seen = handler.ConnectTimeout;
        };

        // Act
        IWebApplicationSetting<System.Collections.Generic.IReadOnlyList<ReverseProxyRoute>> setting = builder.Build();

        // Assert — the callback saw the proxy's own defaults, which it may override.
        seen.ShouldBe(TimeSpan.FromSeconds(15));
        ((IDisposable)setting).Dispose();
    }
}
