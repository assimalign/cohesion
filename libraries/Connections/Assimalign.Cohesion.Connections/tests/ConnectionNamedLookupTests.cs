using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public sealed class ConnectionNamedLookupTests
{
    [Fact(DisplayName = "Cohesion Test [Connections] - Named Get: Custom subclass lookup remains authoritative")]
    public void Get_CustomSubclass_ShouldPreserveInterfaceDispatchAndLocalShadowing()
    {
        // Arrange
        TestConnectionFeature selected = new("Custom");
        NamedLookupCollection custom = new(selected);
        IConnectionFeatureCollection features = new ConnectionFeatureCollection(new ConnectionFeatureCollection(custom));
        TestConnectionFeature own = new("Local");
        features.Set(own);

        // Act and assert
        features.Get("Custom").ShouldBeSameAs(selected);
        features.Get("BaseOnly").ShouldBeNull();
        features.Get("Local").ShouldBeSameAs(own);
        custom.Calls.ShouldBe(2);

        features.Remove("Custom").ShouldBeTrue();
        features.Get("Custom").ShouldBeNull();
        custom.Calls.ShouldBe(3);
        features.Set(new TestConnectionFeature("Custom"));
        features.Get("Custom").ShouldNotBeSameAs(selected);
        custom.Calls.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Connections] - Named Get: Built-in layers observe live inner replacement and tombstones")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void Get_BuiltInLayers_ShouldObserveLiveInnerChanges(int layers)
    {
        // Arrange
        ConnectionFeatureCollection root = new();
        TestConnectionFeature original = new("Target");
        root.Set(original);
        IConnectionFeatureCollection features = root;
        for (int index = 0; index < layers; index++)
        {
            ConnectionFeatureCollection wrapper = new(features);
            wrapper.Set(new TestConnectionFeature("Layer" + index));
            features = wrapper;
        }
        TestConnectionFeature replacement = new("Target");

        // Act and assert
        features.Get("Target").ShouldBeSameAs(original);
        features.Get("target").ShouldBeNull();
        features.Get("Absent").ShouldBeNull();
        root.Set(replacement);
        features.Get("Target").ShouldBeSameAs(replacement);
        root.Remove("Target");
        features.Get("Target").ShouldBeNull();
        root.Set(original);
        features.Get("Target").ShouldBeSameAs(original);
    }
}
