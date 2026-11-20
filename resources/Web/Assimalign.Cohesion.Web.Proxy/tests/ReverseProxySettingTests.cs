using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Proxy.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// The <c>reverse-proxy</c> setting: its name and value, a whole-value <c>Set</c> that validates before
/// anything changes, and the forwarder each route uses.
/// </summary>
public class ReverseProxySettingTests
{
    private static readonly Uri _destination = new("http://backend/");

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: The reverse-proxy setting's value is the route list it was built with")]
    public void Build_WithRoutes_ShouldExposeTheRoutes()
    {
        // Arrange
        ReverseProxyBuilder builder = new();
        builder.Options.AddRoute(Route("api", "/api"));

        // Act
        using ReverseProxySetting setting = (ReverseProxySetting)builder.Build();

        // Assert
        setting.Name.ShouldBe("reverse-proxy");
        setting.Value.Count.ShouldBe(1);
        setting.Value[0].Name.ShouldBe("api");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: Routes added after the build do not reach the setting")]
    public void Build_ThenRoutesAdded_ShouldKeepTheSnapshot()
    {
        // Arrange
        ReverseProxyBuilder builder = new();
        builder.Options.AddRoute(Route("api", "/api"));
        using ReverseProxySetting setting = (ReverseProxySetting)builder.Build();

        // Act
        builder.Options.AddRoute(Route("late", "/late"));

        // Assert
        setting.Value.Count.ShouldBe(1);
        setting.Table.Match("localhost", "/late").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: A valid route list applies whole")]
    public void Set_ValidRoutes_ShouldReplaceTheRoutes()
    {
        // Arrange
        ReverseProxyBuilder builder = new();
        builder.Options.AddRoute(Route("api", "/api"));
        using ReverseProxySetting setting = (ReverseProxySetting)builder.Build();

        // Act
        setting.Set([Route("v2", "/v2"), Route("web", "/")]);

        // Assert
        setting.Value.Count.ShouldBe(2);
        setting.Table.Match("localhost", "/v2/items").ShouldNotBeNull().Route.Name.ShouldBe("v2");
        setting.Table.Match("localhost", "/api/items").ShouldNotBeNull().Route.Name.ShouldBe("web");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: An empty route list forwards nothing")]
    public void Set_EmptyList_ShouldEmptyTheTable()
    {
        // Arrange
        ReverseProxyBuilder builder = new();
        builder.Options.AddRoute(Route("api", "/api"));
        using ReverseProxySetting setting = (ReverseProxySetting)builder.Build();

        // Act
        setting.Set([]);

        // Assert
        setting.Value.ShouldBeEmpty();
        setting.Table.IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: A route list with one bad route is refused whole, naming the route")]
    public void Set_OneInvalidRoute_ShouldThrowAndKeepTheRoutes()
    {
        // Arrange
        ReverseProxyBuilder builder = new();
        builder.Options.AddRoute(Route("api", "/api"));
        using ReverseProxySetting setting = (ReverseProxySetting)builder.Build();
        ReverseProxyRoute bad = Route("bad", "/bad") with { Destinations = [new Uri("http://backend/?x=1")] };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => setting.Set([Route("v2", "/v2"), bad]));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldContain("'bad'", Case.Sensitive);
        setting.Value.Count.ShouldBe(1);
        setting.Value[0].Name.ShouldBe("api");
        setting.Table.Match("localhost", "/v2").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: A null route list is refused")]
    public void Set_Null_ShouldThrowArgumentNullException()
    {
        // Arrange
        using ReverseProxySetting setting = (ReverseProxySetting)new ReverseProxyBuilder().Build();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => setting.Set(null!)).ParamName.ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: Each route uses the forwarder its PreserveHost asks for")]
    public void GetForwarder_PreserveHost_ShouldFollowTheRouteThenTheOptions()
    {
        // Arrange — the options keep the destination's host; one route overrides that each way.
        ReverseProxyBuilder builder = new();
        builder.Options
            .AddRoute(Route("inherit", "/inherit"))
            .AddRoute(Route("preserve", "/preserve") with { PreserveHost = true })
            .AddRoute(Route("replace", "/replace") with { PreserveHost = false });
        using ReverseProxySetting setting = (ReverseProxySetting)builder.Build();
        IReadOnlyList<ReverseProxyRouteEntry> entries = Entries(setting);

        // Act
        Http.HttpForwarder inherit = setting.GetForwarder(entries[0]);
        Http.HttpForwarder preserve = setting.GetForwarder(entries[1]);
        Http.HttpForwarder replace = setting.GetForwarder(entries[2]);

        // Assert
        preserve.ShouldNotBeSameAs(inherit);
        replace.ShouldBeSameAs(inherit);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Setting: Options that preserve the host apply to routes that do not say")]
    public void GetForwarder_OptionsPreserveHost_ShouldApplyToRoutesThatDoNotSay()
    {
        // Arrange
        ReverseProxyBuilder builder = new();
        builder.Options.Forwarder.PreserveHost = true;
        builder.Options
            .AddRoute(Route("inherit", "/inherit"))
            .AddRoute(Route("preserve", "/preserve") with { PreserveHost = true })
            .AddRoute(Route("replace", "/replace") with { PreserveHost = false });

        // Act
        using ReverseProxySetting setting = (ReverseProxySetting)builder.Build();
        IReadOnlyList<ReverseProxyRouteEntry> entries = Entries(setting);

        // Assert — the build put the options' own PreserveHost back after creating both forwarders.
        setting.GetForwarder(entries[0]).ShouldBeSameAs(setting.GetForwarder(entries[1]));
        setting.GetForwarder(entries[2]).ShouldNotBeSameAs(setting.GetForwarder(entries[1]));
        builder.Options.Forwarder.PreserveHost.ShouldBeTrue();
    }

    private static IReadOnlyList<ReverseProxyRouteEntry> Entries(ReverseProxySetting setting)
    {
        List<ReverseProxyRouteEntry> entries = new();
        foreach (string path in new[] { "/inherit", "/preserve", "/replace" })
        {
            entries.Add(setting.Table.Match("localhost", path).ShouldNotBeNull());
        }

        return entries;
    }

    private static ReverseProxyRoute Route(string name, string prefix)
    {
        return new ReverseProxyRoute { Name = name, PathPrefix = prefix, Destinations = [_destination] };
    }
}
