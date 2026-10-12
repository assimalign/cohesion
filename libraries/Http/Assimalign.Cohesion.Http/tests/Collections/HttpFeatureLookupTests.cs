using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpFeatureLookupTests
{
    private readonly ITestOutputHelper _output;

    public HttpFeatureLookupTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Get: First lookup on a presized exchange allocates nothing")]
    [InlineData(4)]
    [InlineData(16)]
    public void Get_FreshPresizedCollections_ShouldNotAllocate(int capacity)
    {
        // Arrange — warm the code, but not the per-exchange collection.
        _ = CreateFeatures(capacity).Get<ISampleFeature>();
        IHttpFeatureCollection[] exchanges = CreateExchanges(capacity);
        ISampleFeature? resolved = null;

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (IHttpFeatureCollection features in exchanges)
        {
            resolved = features.Get<ISampleFeature>();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        resolved.ShouldNotBeNull();
        allocated.ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Set: First typed removal on a presized exchange allocates nothing")]
    [InlineData(4)]
    [InlineData(16)]
    public void Set_FreshPresizedCollections_ShouldNotAllocate(int capacity)
    {
        // Arrange
        CreateFeatures(capacity).Set<ISampleFeature>(null);
        IHttpFeatureCollection[] exchanges = CreateExchanges(capacity);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (IHttpFeatureCollection features in exchanges)
        {
            features.Set<ISampleFeature>(null);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        exchanges[0].Get<ISampleFeature>().ShouldBeNull();
        allocated.ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Get: Eight lookups per exchange eliminate LINQ lookup allocations")]
    [InlineData(4)]
    [InlineData(16)]
    public void Get_EightLookupsPerExchange_ShouldEliminateLookupAllocations(int capacity)
    {
        // Arrange — compare fresh presized collections; construction/stamping is outside the sample.
        IHttpFeatureCollection[] baseline = CreateExchanges(capacity);
        IHttpFeatureCollection[] optimized = CreateExchanges(capacity);
        IHttpFeatureCollection warmup = CreateFeatures(capacity);
        for (int i = 0; i < 100; i++)
        {
            _ = warmup.OfType<ISampleFeature>().FirstOrDefault();
            _ = warmup.OfType<IMissingFeature>().FirstOrDefault();
            _ = warmup.Get<ISampleFeature>();
            _ = warmup.Get<IMissingFeature>();
        }

        // Act — four hits and four misses per request.
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (IHttpFeatureCollection features in baseline)
        {
            for (int i = 0; i < 4; i++)
            {
                _ = features.OfType<ISampleFeature>().FirstOrDefault();
                _ = features.OfType<IMissingFeature>().FirstOrDefault();
            }
        }
        long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        foreach (IHttpFeatureCollection features in optimized)
        {
            for (int i = 0; i < 4; i++)
            {
                _ = features.Get<ISampleFeature>();
                _ = features.Get<IMissingFeature>();
            }
        }
        long optimizedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        _output.WriteLine($"{capacity} features, 8 lookups/exchange: LINQ {baselineBytes / baseline.Length} B/exchange; Get<T> {optimizedBytes / optimized.Length} B/exchange.");
        baselineBytes.ShouldBeGreaterThan(0L);
        optimizedBytes.ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Set: Typed removal preserves first-match order and defaults visibility")]
    public void Set_MultipleMatchesAndDefaults_ShouldRemoveOnlyFirstLocalMatch()
    {
        // Arrange
        HttpFeatureCollection defaults = new();
        SampleFeature fallback = new("first");
        defaults.Set(fallback);
        HttpFeatureCollection features = new(defaults);
        features.Set(new SampleFeature("first"));
        SampleFeature second = new("second");
        features.Set(second);

        // Act
        features.Set<ISampleFeature>(null);
        ISampleFeature? afterFirst = features.Get<ISampleFeature>();
        features.Set<ISampleFeature>(null);
        ISampleFeature? afterSecond = features.Get<ISampleFeature>();
        features.Set<ISampleFeature>(null);

        // Assert — removal never mutates the defaults source.
        afterFirst.ShouldBeSameAs(second);
        afterSecond.ShouldBeSameAs(fallback);
        features.Get<ISampleFeature>().ShouldBeSameAs(fallback);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Get: A derived collection keeps its custom enumeration order and disposal")]
    public void Get_DerivedCollectionWithCustomOrder_ShouldUseAndDisposeItsEnumerator()
    {
        // Arrange
        SampleFeature visible = new("custom");
        CustomFeatureCollection custom = new(visible);
        custom.Set(new SampleFeature("base"));
        IHttpFeatureCollection features = custom;
        HttpFeatureCollection overlay = new(custom);

        // Act
        ISampleFeature? direct = features.Get<ISampleFeature>();
        ISampleFeature? fallback = overlay.Get<ISampleFeature>();

        // Assert
        direct.ShouldBeSameAs(visible);
        fallback.ShouldBeSameAs(visible);
        custom.Disposals.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Get: Replacement and reused slots preserve enumeration precedence")]
    public void Get_AfterRemovalAndReplacement_ShouldMatchEnumeration()
    {
        // Arrange
        HttpFeatureCollection features = new();
        features.Set(new SampleFeature("first"));
        features.Set(new SampleFeature("second"));
        features.Remove("first");
        features.Set(new SampleFeature("reused"));
        features.Set(new SampleFeature("second"));

        // Act
        ISampleFeature? resolved = features.Get<ISampleFeature>();

        // Assert
        resolved.ShouldBeSameAs(features.OfType<ISampleFeature>().First());
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Get: Custom collections retain LINQ's non-generic enumeration precedence")]
    public void Get_DifferentEnumerationOrders_ShouldUseNonGenericOrder()
    {
        // Arrange
        IHttpFeatureCollection features = new SplitEnumeratorFeatureCollection();
        SampleFeature genericFirst = new("generic");
        SampleFeature nonGenericFirst = new("nonGeneric");
        features.Set(genericFirst);
        features.Set(nonGenericFirst);

        // Act
        ISampleFeature? resolved = features.Get<ISampleFeature>();

        // Assert
        resolved.ShouldBeSameAs(nonGenericFirst);
        resolved.ShouldBeSameAs(features.OfType<ISampleFeature>().First());
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Set: Custom collections remove the first non-generic enumeration match")]
    public void Set_DifferentEnumerationOrders_ShouldRemoveNonGenericFirstMatch()
    {
        // Arrange
        IHttpFeatureCollection features = new SplitEnumeratorFeatureCollection();
        SampleFeature genericFirst = new("generic");
        features.Set(genericFirst);
        features.Set(new SampleFeature("nonGeneric"));

        // Act
        features.Set<ISampleFeature>(null);

        // Assert
        features.Get("nonGeneric").ShouldBeNull();
        features.Get("generic").ShouldBeSameAs(genericFirst);
    }

    private sealed class SplitEnumeratorFeatureCollection : HttpFeatureCollection, IEnumerable<IHttpFeature>
    {
        IEnumerator<IHttpFeature> IEnumerable<IHttpFeature>.GetEnumerator()
        {
            yield return Get("generic")!;
            yield return Get("nonGeneric")!;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            yield return Get("nonGeneric");
            yield return Get("generic");
        }
    }

    private sealed class CustomFeatureCollection : HttpFeatureCollection, IEnumerable<IHttpFeature>
    {
        private readonly IHttpFeature _visible;

        public CustomFeatureCollection(IHttpFeature visible)
        {
            _visible = visible;
        }

        public int Disposals { get; private set; }

        IEnumerator<IHttpFeature> IEnumerable<IHttpFeature>.GetEnumerator()
        {
            try
            {
                yield return _visible;
            }
            finally
            {
                Disposals++;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<IHttpFeature>)this).GetEnumerator();
    }

    private static IHttpFeatureCollection[] CreateExchanges(int capacity)
    {
        IHttpFeatureCollection[] exchanges = new IHttpFeatureCollection[1_000];
        for (int i = 0; i < exchanges.Length; i++)
        {
            exchanges[i] = CreateFeatures(capacity);
        }
        return exchanges;
    }

    private static IHttpFeatureCollection CreateFeatures(int capacity)
    {
        HttpFeatureCollection features = new(capacity);
        for (int i = 1; i < capacity; i++)
        {
            features.Set(new OtherFeature(i.ToString()));
        }
        features.Set(new SampleFeature("sample"));
        return features;
    }

    private interface ISampleFeature : IHttpFeature { }
    private interface IMissingFeature : IHttpFeature { }

    private sealed class SampleFeature : ISampleFeature
    {
        public SampleFeature(string name) { Name = name; }
        public string Name { get; }
    }

    private sealed class OtherFeature : IHttpFeature
    {
        public OtherFeature(string name) { Name = name; }
        public string Name { get; }
    }
}
