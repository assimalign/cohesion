using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Web.Proxy.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// A route's transforms: the path it forwards, the destination it takes in turn, and the snapshot it
/// keeps of the route it was compiled from.
/// </summary>
public class ReverseProxyRouteEntryTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Route: A route that strips its prefix forwards the path below it")]
    [InlineData("/api", "/api", "/")]
    [InlineData("/api", "/api/", "/")]
    [InlineData("/api", "/api/users/7", "/users/7")]
    [InlineData("/api/", "/API/users", "/users")]
    [InlineData("/", "/users", "/users")]
    public void GetForwardedPath_StripPrefix_ShouldForwardThePathBelowThePrefix(string prefix, string path, string expected)
    {
        // Arrange
        ReverseProxyRouteEntry entry = Create(new ReverseProxyRoute
        {
            Name = "api",
            PathPrefix = prefix,
            StripPrefix = true,
            Destinations = [new Uri("http://backend/")],
        });

        // Act
        string forwarded = entry.GetForwardedPath(path);

        // Assert
        forwarded.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Route: A route that keeps its prefix forwards the whole path")]
    public void GetForwardedPath_KeepPrefix_ShouldForwardTheWholePath()
    {
        // Arrange
        ReverseProxyRouteEntry entry = Create(new ReverseProxyRoute
        {
            Name = "api",
            PathPrefix = "/api",
            Destinations = [new Uri("http://backend/")],
        });

        // Act
        string forwarded = entry.GetForwardedPath("/api/users");

        // Assert
        forwarded.ShouldBe("/api/users");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Route: Destinations are taken round-robin, in the order they are listed")]
    public void NextDestination_SeveralDestinations_ShouldRotateInOrder()
    {
        // Arrange
        Uri a = new("http://a/");
        Uri b = new("http://b/");
        Uri c = new("http://c/");
        ReverseProxyRouteEntry entry = Create(new ReverseProxyRoute { Name = "pool", Destinations = [a, b, c] });

        // Act
        Uri[] taken = Enumerable.Range(0, 7).Select(_ => entry.NextDestination()).ToArray();

        // Assert
        taken.ShouldBe([a, b, c, a, b, c, a]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Route: Concurrent requests share the destinations evenly")]
    public void NextDestination_ConcurrentCallers_ShouldShareTheDestinationsEvenly()
    {
        // Arrange
        Uri a = new("http://a/");
        Uri b = new("http://b/");
        ReverseProxyRouteEntry entry = Create(new ReverseProxyRoute { Name = "pool", Destinations = [a, b] });
        Uri[] taken = new Uri[10_000];

        // Act
        System.Threading.Tasks.Parallel.For(0, taken.Length, i => taken[i] = entry.NextDestination());

        // Assert — each turn is taken exactly once, so the split is exact.
        taken.Count(destination => destination == a).ShouldBe(5_000);
        taken.Count(destination => destination == b).ShouldBe(5_000);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Route: The compiled route keeps a copy of its lists")]
    public void Create_ListsChangedAfterward_ShouldKeepTheSnapshot()
    {
        // Arrange
        List<string> hosts = ["api.example.com"];
        List<Uri> destinations = [new Uri("http://a/")];
        ReverseProxyRouteEntry entry = Create(new ReverseProxyRoute { Name = "api", Hosts = hosts, Destinations = destinations });

        // Act
        hosts.Add("evil.example");
        destinations.Add(new Uri("http://evil/"));

        // Assert
        entry.Route.Hosts.ShouldBe(["api.example.com"]);
        entry.Route.Destinations.ShouldBe([new Uri("http://a/")]);
        entry.MatchHost("evil.example").ShouldBe(-1);
        entry.NextDestination().ShouldBe(new Uri("http://a/"));
        entry.NextDestination().ShouldBe(new Uri("http://a/"));
    }

    private static ReverseProxyRouteEntry Create(ReverseProxyRoute route)
    {
        return ReverseProxyRouteEntry.Create(route, 0, "route");
    }
}
