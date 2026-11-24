using System;

using Assimalign.Cohesion.Web.Proxy.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// Route selection: the closest host match, then the longest path prefix on a segment boundary, then
/// the route registered first.
/// </summary>
public class ReverseProxyRouteTableTests
{
    private static readonly Uri _destination = new("http://backend/");

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Route table: The closest host pattern wins before the longest prefix")]
    [InlineData("api.example.com", "/v1/users", "exact")]
    [InlineData("API.Example.com:8443", "/v1/users", "exact")]
    [InlineData("x.api.example.com", "/v1/users", "deep-wildcard")]
    [InlineData("shop.example.com", "/v1/users", "wildcard")]
    [InlineData("other.test", "/v1/users/7", "any-host")]
    [InlineData("example.com", "/v1/users", "any-host")]
    public void Match_HostRanking_ShouldPreferTheClosestHost(string host, string path, string expected)
    {
        // Arrange — the less specific the host pattern, the longer the prefix, so prefix length alone
        // would pick the wrong route every time. '*.example.com' does not cover example.com itself.
        ReverseProxyRouteTable table = ReverseProxyRouteTable.Create(
        [
            Route("any-host", "/v1/users"),
            Route("wildcard", "/v1", "*.example.com"),
            Route("deep-wildcard", "/", "*.api.example.com"),
            Route("exact", "/", "api.example.com"),
        ], "routes");

        // Act
        ReverseProxyRouteEntry? route = table.Match(host, path);

        // Assert
        route.ShouldNotBeNull().Route.Name.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Route table: The longest prefix that ends on a segment boundary wins")]
    [InlineData("/", "root")]
    [InlineData("/api", "api")]
    [InlineData("/api/", "api")]
    [InlineData("/api/orders", "api")]
    [InlineData("/api/users", "users")]
    [InlineData("/api/users/7", "users")]
    [InlineData("/API/Users/7", "users")]
    [InlineData("/apix", "root")]
    [InlineData("/api/usersx", "api")]
    public void Match_PathPrefixes_ShouldPreferTheLongestOnASegmentBoundary(string path, string expected)
    {
        // Arrange
        ReverseProxyRouteTable table = ReverseProxyRouteTable.Create(
        [
            Route("root", "/"),
            Route("users", "/api/users/"),
            Route("api", "/api"),
        ], "routes");

        // Act
        ReverseProxyRouteEntry? route = table.Match("localhost", path);

        // Assert
        route.ShouldNotBeNull().Route.Name.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Route table: Of two equally specific routes the one registered first wins")]
    public void Match_EquallySpecificRoutes_ShouldPreferTheFirstRegistered()
    {
        // Arrange
        ReverseProxyRouteTable table = ReverseProxyRouteTable.Create(
        [
            Route("first", "/shop", "*.example.com"),
            Route("second", "/shop", "*.example.com"),
        ], "routes");

        // Act
        ReverseProxyRouteEntry? route = table.Match("a.example.com", "/shop/cart");

        // Assert
        route.ShouldNotBeNull().Route.Name.ShouldBe("first");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Route table: A request no route takes matches nothing")]
    [InlineData("shop.example.com", "/v2")]
    [InlineData("other.test", "/v1")]
    [InlineData("example.com", "/v1")]
    public void Match_NoRouteTakesTheRequest_ShouldReturnNull(string host, string path)
    {
        // Arrange
        ReverseProxyRouteTable table = ReverseProxyRouteTable.Create(
        [
            Route("wildcard", "/v1", "*.example.com"),
            Route("any-host", "/v1/users"),
        ], "routes");

        // Act
        ReverseProxyRouteEntry? route = table.Match(host, path);

        // Assert
        route.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Route table: An empty table is empty and matches nothing")]
    public void Create_NoRoutes_ShouldBeEmpty()
    {
        // Arrange
        ReverseProxyRouteTable table = ReverseProxyRouteTable.Create([], "routes");

        // Act
        ReverseProxyRouteEntry? route = table.Match("localhost", "/");

        // Assert
        table.IsEmpty.ShouldBeTrue();
        table.Routes.ShouldBeEmpty();
        route.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Route table: A null route in a value is refused with its index")]
    public void Create_NullRoute_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => ReverseProxyRouteTable.Create([Route("api", "/api"), null!], "value"));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldContain("index 1", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Route table: Two routes with one name in a value are refused with the name")]
    public void Create_DuplicateNames_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => ReverseProxyRouteTable.Create([Route("api", "/api"), Route("api", "/v2")], "value"));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldContain("'api'", Case.Sensitive);
    }

    private static ReverseProxyRoute Route(string name, string prefix, params string[] hosts)
    {
        return new ReverseProxyRoute
        {
            Name = name,
            Hosts = hosts,
            PathPrefix = prefix,
            Destinations = [_destination],
        };
    }
}
