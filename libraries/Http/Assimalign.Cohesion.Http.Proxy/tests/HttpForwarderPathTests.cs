using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public sealed class HttpForwarderPathTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - TryNormalizePath: Should remove dot segments")]
    [InlineData("/a/b", "/a/b")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/../b", "/b")]
    [InlineData("/../a", "/a")]
    [InlineData("/a/b/../../c", "/c")]
    [InlineData("/a/..", "/")]
    [InlineData("/a/.", "/a/")]
    [InlineData("/a/b/", "/a/b/")]
    [InlineData("", "/")]
    [InlineData("*", "/")]
    [InlineData("/a%2Fb", "/a%2Fb")]
    [InlineData("/a..b/.c", "/a..b/.c")]
    public void TryNormalizePath_DotSegments_ShouldRemoveThem(string path, string expected)
    {
        // Arrange / Act
        bool normalized = HttpForwarder.TryNormalizePath(new HttpPath(path), out HttpPath result);

        // Assert
        normalized.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - TryNormalizePath: Should refuse a dot segment hidden behind an encoded slash or a backslash")]
    [InlineData("/api/x%2F..%2Fadmin")]
    [InlineData("/api/x%2f..%2fadmin")]
    [InlineData("/api/..%2Fadmin")]
    [InlineData("/api/x%2F..")]
    [InlineData("/api/..\\admin")]
    [InlineData("/api\\..\\admin")]
    [InlineData("/api/x\\.\\admin")]
    public void TryNormalizePath_HiddenDotSegment_ShouldRefuse(string path)
    {
        // Arrange / Act
        bool normalized = HttpForwarder.TryNormalizePath(new HttpPath(path), out _);

        // Assert
        normalized.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - CreateDestination: Should join the prefix, the path and the query the client sent")]
    [InlineData("http://backend:8080", "/api/items", "?a=1&b=2", "http://backend:8080/api/items?a=1&b=2")]
    [InlineData("https://backend/v1/", "/items", "", "https://backend/v1/items")]
    [InlineData("https://backend/v1", "/items", "", "https://backend/v1/items")]
    [InlineData("http://backend:80/", "/x", "", "http://backend/x")]
    [InlineData("http://backend", "", "", "http://backend/")]
    [InlineData("http://backend/base", "*", "", "http://backend/base/")]
    [InlineData("http://backend", "/caf\u00e9/c%2Fd", "", "http://backend/caf%C3%A9/c%2Fd")]
    [InlineData("http://backend", "/100%", "", "http://backend/100%25")]
    [InlineData("http://backend", "/a\"b", "", "http://backend/a%22b")]
    [InlineData("http://backend", "/s", "?q=caf%C3%A9&x=%2F&flag", "http://backend/s?q=caf%C3%A9&x=%2F&flag")]
    [InlineData("http://backend", "/s", "?q=a b", "http://backend/s?q=a%20b")]
    public void CreateDestination_ParsedQuery_ShouldBuildUri(string prefix, string path, string query, string expected)
    {
        // Arrange
        HttpQueryCollection parsed = new HttpQuery(query).Parse();

        // Act
        Uri destination = HttpForwarder.CreateDestination(new Uri(prefix), new HttpPath(path), parsed);

        // Assert
        destination.AbsoluteUri.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - CreateDestination: Should encode a query the application built or changed")]
    public void CreateDestination_ChangedQuery_ShouldEncodeCollection()
    {
        // Arrange
        HttpQueryCollection query = new HttpQuery("?a=1").Parse();
        query.Remove("a");
        query.Add("q", "a b&c");

        // Act
        Uri destination = HttpForwarder.CreateDestination(new Uri("http://backend"), new HttpPath("/s"), query);

        // Assert
        query.Source.ShouldBeNull();
        destination.AbsoluteUri.ShouldBe("http://backend/s?q=a%20b%26c");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - CreateDestination: Should reject a prefix that is not a bare HTTP base")]
    [InlineData("ftp://backend/")]
    [InlineData("http://backend/?q=1")]
    [InlineData("http://backend/#fragment")]
    [InlineData("http://user:secret@backend/")]
    public void CreateDestination_InvalidPrefix_ShouldThrow(string prefix)
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => HttpForwarder.CreateDestination(new Uri(prefix), new HttpPath("/"), new HttpQueryCollection()))
            .ParamName.ShouldBe("destinationPrefix");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - IsValidDestinationPrefix: Should accept a bare HTTP base")]
    [InlineData("http://backend")]
    [InlineData("https://backend:8443/v1/")]
    [InlineData("HTTP://backend/base")]
    [InlineData("http://[::1]:8080")]
    public void IsValidDestinationPrefix_HttpBase_ShouldAccept(string prefix)
    {
        // Arrange / Act
        bool valid = HttpForwarder.IsValidDestinationPrefix(new Uri(prefix));

        // Assert
        valid.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - IsValidDestinationPrefix: Should refuse what CreateDestination refuses")]
    [InlineData("ftp://backend/")]
    [InlineData("http://backend/?q=1")]
    [InlineData("http://backend/#fragment")]
    [InlineData("http://user:secret@backend/")]
    public void IsValidDestinationPrefix_NotBareHttpBase_ShouldRefuse(string prefix)
    {
        // Arrange / Act
        bool valid = HttpForwarder.IsValidDestinationPrefix(new Uri(prefix));

        // Assert
        valid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - IsValidDestinationPrefix: Should refuse a relative or missing prefix")]
    public void IsValidDestinationPrefix_RelativeOrNull_ShouldRefuse()
    {
        // Arrange / Act / Assert
        HttpForwarder.IsValidDestinationPrefix(new Uri("/base", UriKind.Relative)).ShouldBeFalse();
        HttpForwarder.IsValidDestinationPrefix(null).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - CreateDestination: Should reject a relative prefix")]
    public void CreateDestination_RelativePrefix_ShouldThrow()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => HttpForwarder.CreateDestination(new Uri("/base", UriKind.Relative), new HttpPath("/"), new HttpQueryCollection()));
    }

    [Theory(DisplayName = "Cohesion Test [Http.Proxy] - CreateDestination: Should reject a path that was not normalized")]
    [InlineData("/a/../b")]
    [InlineData("/a/./b")]
    [InlineData("/api/x%2F..%2Fadmin")]
    public void CreateDestination_UnnormalizedPath_ShouldThrow(string path)
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => HttpForwarder.CreateDestination(new Uri("http://backend"), new HttpPath(path), new HttpQueryCollection()))
            .ParamName.ShouldBe("path");
    }
}
