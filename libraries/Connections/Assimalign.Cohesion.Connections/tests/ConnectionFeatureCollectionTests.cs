using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public class ConnectionFeatureCollectionTests
{
    [Fact(DisplayName = "Cohesion Test [Connections] - Get: Wrapped features pass through by name and type")]
    public void Get_WithInnerFeature_ShouldPassThrough()
    {
        // Arrange
        var feature = new TestConnectionFeature("Shared");
        var inner = new ConnectionFeatureCollection();
        inner.Set(feature);
        IConnectionFeatureCollection features = new ConnectionFeatureCollection(inner);

        // Act and assert
        features.Get("Shared").ShouldBeSameAs(feature);
        features.Get<TestConnectionFeature>().ShouldBeSameAs(feature);
        features.Get("shared").ShouldBeNull();
        features.Get("Absent").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Set: Replaces the named slot regardless of feature type")]
    public void Set_WithSameName_ShouldReplaceLocalAndHideInner()
    {
        // Arrange
        var inner = new ConnectionFeatureCollection();
        inner.Set(new TestConnectionFeature("Shared"));
        IConnectionFeatureCollection features = new ConnectionFeatureCollection(inner);
        features.Set(new TestConnectionFeature("Shared"));
        var replacement = new OtherFeature("Shared");

        // Act
        features.Set(replacement);

        // Assert
        features.Get("Shared").ShouldBeSameAs(replacement);
        features.Get<TestConnectionFeature>().ShouldBeNull();
        features.ShouldHaveSingleItem().ShouldBeSameAs(replacement);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Remove: Hides a wrapped feature without changing the wrapped collection")]
    public void Remove_WithInnerFeature_ShouldHideName()
    {
        // Arrange
        var feature = new TestConnectionFeature("Shared");
        var inner = new ConnectionFeatureCollection();
        inner.Set(feature);
        IConnectionFeatureCollection features = new ConnectionFeatureCollection(inner);

        // Act
        bool removed = features.Remove("Shared");

        // Assert
        removed.ShouldBeTrue();
        features.Remove("Shared").ShouldBeFalse();
        features.Get("Shared").ShouldBeNull();
        features.Get<TestConnectionFeature>().ShouldBeNull();
        features.ShouldBeEmpty();
        inner.Get("Shared").ShouldBeSameAs(feature);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Remove: Removing a replacement also hides its wrapped slot")]
    public void Remove_WithReplacement_ShouldNotRevealInner()
    {
        // Arrange
        var inner = new ConnectionFeatureCollection();
        inner.Set(new TestConnectionFeature("Shared"));
        var features = new ConnectionFeatureCollection(inner);
        features.Set(new OtherFeature("Shared"));

        // Act
        bool removed = features.Remove("Shared");

        // Assert
        removed.ShouldBeTrue();
        features.Get("Shared").ShouldBeNull();
        features.Get<TestConnectionFeature>().ShouldBeNull();
        features.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Remove: A hidden name stays hidden when the inner collection changes")]
    public void Remove_WithAbsentName_ShouldHideFutureInnerFeatureUntilSet()
    {
        // Arrange
        var inner = new ConnectionFeatureCollection();
        var features = new ConnectionFeatureCollection(inner);
        var replacement = new TestConnectionFeature("Future");

        // Act
        bool removed = features.Remove("Future");
        inner.Set(new TestConnectionFeature("Future"));

        // Assert
        removed.ShouldBeFalse();
        features.Get("Future").ShouldBeNull();
        features.Get<TestConnectionFeature>().ShouldBeNull();
        features.Set(replacement);
        features.Get("Future").ShouldBeSameAs(replacement);
        features.ShouldHaveSingleItem().ShouldBeSameAs(replacement);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Get: Own features precede different-named wrapped features of the same type")]
    public void Get_WithMultipleLayers_ShouldFindOwnFeatureBeforeInner()
    {
        // Arrange
        var inner = new ConnectionFeatureCollection();
        inner.Set(new TestConnectionFeature("Deep"));
        var middle = new ConnectionFeatureCollection(inner);
        var nearest = new TestConnectionFeature("Middle");
        middle.Set(nearest);
        var features = new ConnectionFeatureCollection(middle);
        var own = new TestConnectionFeature("Own");
        features.Set(own);

        // Act and assert
        features.Get<TestConnectionFeature>().ShouldBeSameAs(own);
        features.Remove("Own");
        features.Get<TestConnectionFeature>().ShouldBeSameAs(nearest);
        features.Remove("Middle");
        features.Get<TestConnectionFeature>().ShouldBeSameAs(inner.Get("Deep"));
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - GetEnumerator: Returns own features then only effective wrapped features")]
    public void GetEnumerator_WithReplacementAndRemoval_ShouldYieldEffectiveSetInOrder()
    {
        // Arrange
        var inner = new ConnectionFeatureCollection();
        inner.Set(new TestConnectionFeature("Replaced"));
        inner.Set(new TestConnectionFeature("Hidden"));
        var visible = new TestConnectionFeature("Visible");
        inner.Set(visible);
        var middle = new ConnectionFeatureCollection(inner);
        var replacement = new OtherFeature("Replaced");
        middle.Set(replacement);
        middle.Remove("Hidden");
        var features = new ConnectionFeatureCollection(middle);
        var own = new TestConnectionFeature("Own");
        features.Set(own);

        // Act
        IConnectionFeature[] effective = features.ToArray();
        object[] untyped = ((IEnumerable)features).Cast<object>().ToArray();

        // Assert
        effective.ShouldBe(new IConnectionFeature[] { own, replacement, visible });
        untyped.ShouldBe(new object[] { own, replacement, visible });
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - GetEnumerator: In-flight enumeration retains its local snapshot")]
    public void GetEnumerator_WithConcurrentMutation_ShouldRetainLocalSnapshot()
    {
        // Arrange
        var features = new ConnectionFeatureCollection();
        var first = new TestConnectionFeature("First");
        var second = new TestConnectionFeature("Second");
        features.Set(first);
        features.Set(second);
        using IEnumerator<IConnectionFeature> iterator = features.GetEnumerator();
        iterator.MoveNext().ShouldBeTrue();

        // Act
        features.Remove("Second");
        features.Set(new TestConnectionFeature("Third"));

        // Assert
        iterator.Current.ShouldBeSameAs(first);
        iterator.MoveNext().ShouldBeTrue();
        iterator.Current.ShouldBeSameAs(second);
        iterator.MoveNext().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Set: Rejects null features and empty names")]
    public void Set_WithInvalidFeature_ShouldThrow()
    {
        // Arrange
        IConnectionFeatureCollection features = new ConnectionFeatureCollection();

        // Act and assert
        Should.Throw<ArgumentNullException>(() => features.Set<TestConnectionFeature>(null!));
        Should.Throw<ArgumentException>(() => features.Set(new TestConnectionFeature(string.Empty)));
        Should.Throw<ArgumentNullException>(() => features.Set(new TestConnectionFeature(null!)));
        features.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Connections] - Named access: Rejects null and empty names")]
    [InlineData(null)]
    [InlineData("")]
    public void GetAndRemove_WithInvalidName_ShouldThrow(string? name)
    {
        // Arrange
        IConnectionFeatureCollection features = new ConnectionFeatureCollection();

        // Act and assert
        Should.Throw<ArgumentException>(() => features.Get(name!));
        Should.Throw<ArgumentException>(() => features.Remove(name!));
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Get: Rejects a null collection")]
    public void Get_WithNullCollection_ShouldThrow()
    {
        // Arrange
        IConnectionFeatureCollection features = null!;

        // Act and assert
        Should.Throw<ArgumentNullException>(() => features.Get<TestConnectionFeature>());
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Get: Respects custom collection enumeration and outer hiding")]
    public void Get_WithCustomInnerCollection_ShouldRespectEffectiveEnumeration()
    {
        // Arrange
        var hidden = new TestConnectionFeature("Hidden");
        var visible = new TestConnectionFeature("Visible");
        IConnectionFeatureCollection custom = new CustomCollection(hidden, visible);
        var features = new ConnectionFeatureCollection(custom);
        features.Remove("Hidden");

        // Act and assert
        custom.Get<TestConnectionFeature>().ShouldBeSameAs(hidden);
        features.Get<TestConnectionFeature>().ShouldBeSameAs(visible);
        features.ShouldHaveSingleItem().ShouldBeSameAs(visible);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Get: Layered typed lookups allocate no memory")]
    public void Get_WithConcreteLayers_ShouldAllocateNothing()
    {
        // Arrange
        var inner = new ConnectionFeatureCollection();
        inner.Set(new TestConnectionFeature("Hidden"));
        var match = new TestConnectionFeature("Visible");
        inner.Set(match);
        var middle = new ConnectionFeatureCollection(inner);
        middle.Remove("Hidden");
        IConnectionFeatureCollection features = new ConnectionFeatureCollection(middle);
        for (int index = 0; index < 1000; index++)
        {
            features.Get<TestConnectionFeature>();
            features.Get<OtherFeature>();
        }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        TestConnectionFeature? result = null;
        OtherFeature? absent = null;
        for (int index = 0; index < 1000; index++)
        {
            result = features.Get<TestConnectionFeature>();
            absent = features.Get<OtherFeature>();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        result.ShouldBeSameAs(match);
        absent.ShouldBeNull();
        allocated.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Features: Concurrent mutations retain independent registrations and safe reads")]
    public async Task SetAndRemove_WithConcurrentReaders_ShouldRemainSafe()
    {
        // Arrange
        var inner = new ConnectionFeatureCollection();
        inner.Set(new TestConnectionFeature("Inherited"));
        var features = new ConnectionFeatureCollection(inner);
        using var start = new ManualResetEventSlim();
        Task[] writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            start.Wait();
            string name = "Writer" + writer;
            for (int index = 0; index < 1000; index++)
            {
                features.Set(new TestConnectionFeature(name));
                features.Remove(name);
            }
            features.Set(new TestConnectionFeature(name));
        })).ToArray();
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (int index = 0; index < 1000; index++)
            {
                features.Get("Writer0");
                features.Get<TestConnectionFeature>();
                string[] names = features.Select(feature => feature.Name).ToArray();
                names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
            }
        })).ToArray();

        // Act
        start.Set();
        await Task.WhenAll(writers.Concat(readers));

        // Assert
        features.Count().ShouldBe(5);
        for (int writer = 0; writer < 4; writer++)
        {
            features.Get("Writer" + writer).ShouldNotBeNull();
        }
    }

    private sealed class OtherFeature : IConnectionFeature
    {
        public OtherFeature(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    private sealed class CustomCollection : ConnectionFeatureCollection, IEnumerable<IConnectionFeature>
    {
        private readonly IConnectionFeature[] _effective;

        public CustomCollection(params IConnectionFeature[] effective)
        {
            _effective = effective;
            foreach (IConnectionFeature feature in effective.Reverse())
            {
                Set(feature);
            }
        }

        IEnumerator<IConnectionFeature> IEnumerable<IConnectionFeature>.GetEnumerator()
            => ((IEnumerable<IConnectionFeature>)_effective).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _effective.GetEnumerator();
    }
}
