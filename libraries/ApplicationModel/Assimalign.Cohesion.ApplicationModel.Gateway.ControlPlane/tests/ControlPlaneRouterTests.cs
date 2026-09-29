using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

public sealed class ControlPlaneRouterTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Router: Should capture raw parameter values")]
    public void Match_ParameterTemplate_ShouldCaptureRawValues()
    {
        // Arrange
        ControlPlaneRouter router = CreateRouter();

        // Act
        ControlPlaneRouteMatch match = router.Match(
            HttpMethod.Put,
            new HttpPath("/cohesion/v1/resources/Api/commands/Command-1"));

        // Assert
        match.Status.ShouldBe(ControlPlaneRouteMatchStatus.Matched);
        match.Route!.Template.ShouldBe("/cohesion/v1/resources/{name}/commands/{id}");
        match.Route.Method.ShouldBe(HttpMethod.Put);
        match.Values["name"].ShouldBe("Api");
        match.Values["id"].ShouldBe("Command-1");
        match.Values["NAME"].ShouldBe("Api");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Router: Should ignore literal case and redundant slashes")]
    public void Match_LiteralCaseAndRedundantSlashes_ShouldMatch()
    {
        // Arrange
        ControlPlaneRouter router = CreateRouter();

        // Act
        ControlPlaneRouteMatch upper = router.Match(HttpMethod.Get, new HttpPath("/COHESION/V1/APPLICATION"));
        ControlPlaneRouteMatch slashes = router.Match(HttpMethod.Get, new HttpPath("//cohesion//v1/resources/api/"));

        // Assert
        upper.Status.ShouldBe(ControlPlaneRouteMatchStatus.Matched);
        upper.Route!.Template.ShouldBe("/cohesion/v1/application");
        upper.Values.Count.ShouldBe(0);
        slashes.Status.ShouldBe(ControlPlaneRouteMatchStatus.Matched);
        slashes.Route!.Template.ShouldBe("/cohesion/v1/resources/{name}");
        slashes.Values["name"].ShouldBe("api");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Router: Should serve HEAD from a GET route")]
    public void Match_HeadRequest_ShouldMatchTheGetRoute()
    {
        // Arrange
        ControlPlaneRouter router = CreateRouter();

        // Act
        ControlPlaneRouteMatch match = router.Match(HttpMethod.Head, new HttpPath("/cohesion/v1/resources/api/commands"));

        // Assert
        match.Status.ShouldBe(ControlPlaneRouteMatchStatus.Matched);
        match.Route!.Method.ShouldBe(HttpMethod.Get);
        match.Values["name"].ShouldBe("api");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Router: Should list the acceptable methods in route order for a 405")]
    public void Match_PathWithOtherMethods_ShouldReportAllowedMethodsInRouteOrder()
    {
        // Arrange
        ControlPlaneRouter router = CreateRouter();

        // Act
        ControlPlaneRouteMatch command = router.Match(HttpMethod.Get, new HttpPath("/cohesion/v1/resources/api/commands/id"));
        ControlPlaneRouteMatch head = router.Match(HttpMethod.Head, new HttpPath("/cohesion/v1/resources/api/commands/id"));
        ControlPlaneRouteMatch application = router.Match(HttpMethod.Post, new HttpPath("/cohesion/v1/application"));

        // Assert
        command.Status.ShouldBe(ControlPlaneRouteMatchStatus.MethodNotAllowed);
        command.Route.ShouldBeNull();
        command.Values.Count.ShouldBe(0);
        command.AllowedMethods.Select(method => method.Value).ShouldBe(["PUT", "DELETE"]);
        head.Status.ShouldBe(ControlPlaneRouteMatchStatus.MethodNotAllowed);
        head.AllowedMethods.Select(method => method.Value).ShouldBe(["PUT", "DELETE"]);
        application.Status.ShouldBe(ControlPlaneRouteMatchStatus.MethodNotAllowed);
        application.AllowedMethods.Select(method => method.Value).ShouldBe(["GET", "HEAD"]);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Router: Should not match paths outside the templates")]
    [InlineData("/")]
    [InlineData("/cohesion/v1")]
    [InlineData("/cohesion/v2/application")]
    [InlineData("/cohesion/v1/application/extra")]
    [InlineData("/cohesion/v1/resources")]
    [InlineData("/cohesion/v1/resources/api/command")]
    [InlineData("/cohesion/v1/resources/api/commands/id/extra")]
    public void Match_UnknownPath_ShouldBeUnmatched(string path)
    {
        // Arrange
        ControlPlaneRouter router = CreateRouter();

        // Act
        ControlPlaneRouteMatch match = router.Match(HttpMethod.Get, new HttpPath(path));

        // Assert
        match.Status.ShouldBe(ControlPlaneRouteMatchStatus.NoMatch);
        match.Route.ShouldBeNull();
        match.AllowedMethods.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway.ControlPlane] - Router: Should reject parameter syntax the matcher does not implement")]
    [InlineData("/cohesion/v1/{name")]
    [InlineData("/cohesion/v1/prefix-{name}")]
    [InlineData("/cohesion/v1/{}")]
    [InlineData("/cohesion/v1/{a{b}}")]
    [InlineData("/cohesion/v1/{id?}")]
    [InlineData("/cohesion/v1/{id=1}")]
    [InlineData("/cohesion/v1/{id:int}")]
    [InlineData("/cohesion/v1/{*rest}")]
    public void Constructor_UnsupportedParameterSegment_ShouldThrow(string template)
    {
        // Act
        Action create = () => _ = new ControlPlaneRoute(HttpMethod.Get, template, NoOpAsync);

        // Assert
        create.ShouldThrow<ArgumentException>();
    }

    private static ControlPlaneRouter CreateRouter() =>
        new(
            new ControlPlaneRoute(HttpMethod.Get, "/cohesion/v1/application", NoOpAsync),
            new ControlPlaneRoute(HttpMethod.Get, "/cohesion/v1/resources/{name}", NoOpAsync),
            new ControlPlaneRoute(HttpMethod.Get, "/cohesion/v1/resources/{name}/commands", NoOpAsync),
            new ControlPlaneRoute(HttpMethod.Put, "/cohesion/v1/resources/{name}/commands/{id}", NoOpAsync),
            new ControlPlaneRoute(HttpMethod.Delete, "/cohesion/v1/resources/{name}/commands/{id}", NoOpAsync));

    private static Task NoOpAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
