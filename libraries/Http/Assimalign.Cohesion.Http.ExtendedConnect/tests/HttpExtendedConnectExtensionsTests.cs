using System;

using Assimalign.Cohesion.Http.ExtendedConnect.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.ExtendedConnect.Tests;

public class HttpExtendedConnectExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - ExtendedConnect: A present :protocol item exposes the feature")]
    public void ExtendedConnect_OnProtocolItemPresent_ShouldExposeFeature()
    {
        // Arrange
        FakeHttpContext context = new();
        context.Items[":protocol"] = "websocket";

        // Act / Assert
        context.IsExtendedConnect.ShouldBeTrue();
        context.ExtendedConnect.ShouldNotBeNull();
        context.ExtendedConnect!.Protocol.ShouldBe("websocket");
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - ExtendedConnect: No :protocol item exposes no feature")]
    public void ExtendedConnect_OnNoProtocolItem_ShouldReturnNull()
    {
        // Arrange
        FakeHttpContext context = new();

        // Act / Assert
        context.IsExtendedConnect.ShouldBeFalse();
        context.ExtendedConnect.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - ExtendedConnect: An empty :protocol item exposes no feature")]
    public void ExtendedConnect_OnEmptyProtocolItem_ShouldReturnNull()
    {
        // Arrange
        FakeHttpContext context = new();
        context.Items[":protocol"] = "";

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
