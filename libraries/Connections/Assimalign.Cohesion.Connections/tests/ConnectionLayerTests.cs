using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public class ConnectionLayerTests
{
    private static readonly EndPoint TestEndPoint = new IPEndPoint(IPAddress.Loopback, 16000);

    [Fact]
    public async Task Use_OnListener_ShouldYieldLayerUpgradedConnection()
    {
        // Arrange
        TestConnection inner = new();
        TestConnectionListener listener = new();
        listener.Enqueue(inner);
        RecordingConnectionLayer layer = new("tls", []);

        // Act
        IConnectionListener layered = listener.Use(layer);
        IConnection accepted = await layered.AcceptAsync();

        // Assert
        LayerWrappedConnection wrapped = accepted.ShouldBeOfType<LayerWrappedConnection>();
        wrapped.LayerName.ShouldBe("tls");
        wrapped.Inner.ShouldBeSameAs(inner);
    }

    [Fact]
    public async Task Use_OnListenerWithTwoLayers_ShouldApplyLayersInRegistrationOrder()
    {
        // Arrange
        TestConnection inner = new();
        TestConnectionListener listener = new();
        listener.Enqueue(inner);
        List<string> upgrades = [];
        RecordingConnectionLayer first = new("first", upgrades);
        RecordingConnectionLayer second = new("second", upgrades);

        // Act
        IConnection accepted = await listener.Use(first).Use(second).AcceptAsync();

        // Assert
        upgrades.ShouldBe(["first", "second"]);

        // The outermost wrapper belongs to the layer registered last.
        LayerWrappedConnection outer = accepted.ShouldBeOfType<LayerWrappedConnection>();
        outer.LayerName.ShouldBe("second");
        LayerWrappedConnection innerWrapper = outer.Inner.ShouldBeOfType<LayerWrappedConnection>();
        innerWrapper.LayerName.ShouldBe("first");
        innerWrapper.Inner.ShouldBeSameAs(inner);
    }

    [Fact]
    public void Capabilities_OnLayeredListener_ShouldReflectLayerDescribe()
    {
        // Arrange
        TestConnectionListener listener = new();
        RecordingConnectionLayer layer = new(
            "tls",
            [],
            describe: static capabilities => capabilities with { Security = ConnectionSecurity.Tls });

        // Act
        IConnectionListener layered = listener.Use(layer);

        // Assert
        layered.Capabilities.ShouldBe(listener.Capabilities with { Security = ConnectionSecurity.Tls });
    }

    [Fact]
    public async Task Use_OnFactory_ShouldYieldLayerUpgradedConnection()
    {
        // Arrange
        TestConnection inner = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(inner);
        RecordingConnectionLayer layer = new("tls", []);

        // Act
        IConnectionFactory layered = factory.Use(layer);
        IConnection connected = await layered.ConnectAsync(TestEndPoint);

        // Assert
        LayerWrappedConnection wrapped = connected.ShouldBeOfType<LayerWrappedConnection>();
        wrapped.Inner.ShouldBeSameAs(inner);
        factory.LastEndPoint.ShouldBeSameAs(TestEndPoint);
    }

    [Fact]
    public async Task Use_OnFactoryWithTwoLayers_ShouldApplyLayersInRegistrationOrder()
    {
        // Arrange
        TestConnection inner = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(inner);
        List<string> upgrades = [];
        RecordingConnectionLayer first = new("first", upgrades);
        RecordingConnectionLayer second = new("second", upgrades);

        // Act
        IConnection connected = await factory.Use(first).Use(second).ConnectAsync(TestEndPoint);

        // Assert
        upgrades.ShouldBe(["first", "second"]);
        LayerWrappedConnection outer = connected.ShouldBeOfType<LayerWrappedConnection>();
        outer.LayerName.ShouldBe("second");
        LayerWrappedConnection innerWrapper = outer.Inner.ShouldBeOfType<LayerWrappedConnection>();
        innerWrapper.LayerName.ShouldBe("first");
        innerWrapper.Inner.ShouldBeSameAs(inner);
    }

    [Fact]
    public void Capabilities_OnLayeredFactory_ShouldReflectLayerDescribe()
    {
        // Arrange
        TestConnectionFactory factory = new();
        RecordingConnectionLayer layer = new(
            "tls",
            [],
            describe: static capabilities => capabilities with { Security = ConnectionSecurity.Tls });

        // Act
        IConnectionFactory layered = factory.Use(layer);

        // Assert
        layered.Capabilities.ShouldBe(factory.Capabilities with { Security = ConnectionSecurity.Tls });
    }

    [Fact]
    public async Task Use_WithPassThroughLayer_ShouldReturnInnerConnectionInstance()
    {
        // Arrange
        TestConnection inner = new();
        TestConnectionListener listener = new();
        listener.Enqueue(inner);
        RecordingConnectionLayer passThrough = new("noop", [], wrapConnection: false);

        // Act
        IConnection accepted = await listener.Use(passThrough).AcceptAsync();

        // Assert
        accepted.ShouldBeSameAs(inner);
    }

    [Fact]
    public async Task Use_OnListener_ShouldDelegateEndPointAndDisposeToInner()
    {
        // Arrange
        TestConnectionListener listener = new();
        RecordingConnectionLayer layer = new("noop", [], wrapConnection: false);
        IConnectionListener layered = listener.Use(layer);

        // Act
        await layered.DisposeAsync();

        // Assert
        layered.EndPoint.ShouldBeSameAs(listener.EndPoint);
        listener.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public void Use_WithNullLayer_ShouldThrowArgumentNullException()
    {
        // Arrange
        TestConnectionListener listener = new();
        TestConnectionFactory factory = new();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => listener.Use(null!));
        Should.Throw<ArgumentNullException>(() => factory.Use(null!));
    }
}
