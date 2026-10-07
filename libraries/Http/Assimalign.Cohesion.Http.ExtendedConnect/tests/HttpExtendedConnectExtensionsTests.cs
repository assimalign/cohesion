using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.ExtendedConnect.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.ExtendedConnect.Tests;

public class HttpExtendedConnectExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - ExtendedConnect: An installed feature is exposed, the same instance on every read")]
    public void ExtendedConnect_OnInstalledFeature_ShouldExposeTheSameFeatureOnEveryRead()
    {
        // Arrange — the transport installs its implementation on the feature collection.
        FakeHttpContext context = new();
        FakeExtendedConnectFeature feature = new("websocket", Stream.Null);
        context.Features.Set(feature);

        // Act
        IHttpExtendedConnectFeature? first = context.ExtendedConnect;
        IHttpExtendedConnectFeature? second = context.ExtendedConnect;

        // Assert
        context.IsExtendedConnect.ShouldBeTrue();
        first.ShouldBeSameAs(feature);
        second.ShouldBeSameAs(feature);
        first!.Protocol.ShouldBe("websocket");
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - ExtendedConnect: Accepting through the accessor returns the feature's tunnel")]
    public async Task ExtendedConnect_OnAccept_ShouldReturnTheFeaturesTunnel()
    {
        // Arrange
        FakeHttpContext context = new();
        await using MemoryStream tunnel = new();
        FakeExtendedConnectFeature feature = new("websocket", tunnel);
        context.Features.Set(feature);

        // Act
        Stream accepted = await context.ExtendedConnect!.AcceptAsync();

        // Assert
        accepted.ShouldBeSameAs(tunnel);
        feature.AcceptCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - ExtendedConnect: No installed feature exposes no feature")]
    public void ExtendedConnect_OnNoFeature_ShouldReturnNull()
    {
        // Arrange
        FakeHttpContext context = new();

        // Act / Assert
        context.IsExtendedConnect.ShouldBeFalse();
        context.ExtendedConnect.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - ExtendedConnect: A :protocol item alone exposes no feature")]
    public void ExtendedConnect_OnProtocolItemWithoutFeature_ShouldReturnNull()
    {
        // Arrange — the former transport bridge published :protocol under Items. The accessors read only
        // the feature collection now: a value there models no capability the transport can honor.
        FakeHttpContext context = new();
        context.Items[":protocol"] = "websocket";

        // Act / Assert
        context.IsExtendedConnect.ShouldBeFalse();
        context.ExtendedConnect.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - ExtendedConnect: A null context throws")]
    public void ExtendedConnect_OnNullContext_ShouldThrowArgumentNullException()
    {
        // Arrange
        IHttpContext context = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => { _ = context.ExtendedConnect; });
        Should.Throw<ArgumentNullException>(() => { _ = context.IsExtendedConnect; });
    }
}
