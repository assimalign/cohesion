using System;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpFeatureCollectionTests
{
    private interface ISampleFeature : IHttpFeature
    {
        string Tag { get; }
    }

    private sealed class SampleFeature : ISampleFeature
    {
        public SampleFeature(string tag)
        {
            Tag = tag;
        }

        public string Name => nameof(SampleFeature);
        public string Tag { get; }
    }

    private interface IOtherFeature : IHttpFeature
    {
    }

    private sealed class OtherFeature : IOtherFeature
    {
        public string Name => nameof(OtherFeature);
    }

    [Fact]
    public void Get_NotRegistered_ShouldReturnNull()
    {
        HttpFeatureCollection features = new();

        ISampleFeature? feature = features.Get<ISampleFeature>();

        feature.ShouldBeNull();
    }

    [Fact]
    public void SetThenGet_ShouldReturnSameInstance()
    {
        HttpFeatureCollection features = new();
        SampleFeature instance = new("alpha");

        features.Set<ISampleFeature>(instance);
        ISampleFeature? retrieved = features.Get<ISampleFeature>();

        retrieved.ShouldBeSameAs(instance);
        retrieved!.Tag.ShouldBe("alpha");
    }

    [Fact]
    public void Set_NullInstance_ShouldRemoveExistingRegistration()
    {
        HttpFeatureCollection features = new();
        features.Set<ISampleFeature>(new SampleFeature("a"));

        features.Set<ISampleFeature>(null);

        features.Get<ISampleFeature>().ShouldBeNull();
    }

    [Fact]
    public void Set_TwiceWithDifferentInstances_ShouldReturnLatest()
    {
        HttpFeatureCollection features = new();
        features.Set<ISampleFeature>(new SampleFeature("first"));
        SampleFeature second = new("second");

        features.Set<ISampleFeature>(second);

        features.Get<ISampleFeature>().ShouldBeSameAs(second);
    }

    [Fact]
    public void DifferentFeatureTypes_ShouldCoexistIndependently()
    {
        HttpFeatureCollection features = new();
        SampleFeature sample = new("s");
        OtherFeature other = new();

        features.Set<ISampleFeature>(sample);
        features.Set<IOtherFeature>(other);

        features.Get<ISampleFeature>().ShouldBeSameAs(sample);
        features.Get<IOtherFeature>().ShouldBeSameAs(other);
    }

    [Fact]
    public void GetByName_ShouldReturnRegisteredFeature()
    {
        // Name-keyed lookup is the primitive contract; Get<T> is a convenience over it.
        HttpFeatureCollection features = new();
        SampleFeature instance = new("named");
        features.Set(instance);

        IHttpFeature? resolved = features.Get(nameof(SampleFeature));

        resolved.ShouldBeSameAs(instance);
    }

    [Fact]
    public void Remove_ByName_ShouldDropRegistration()
    {
        HttpFeatureCollection features = new();
        features.Set(new SampleFeature("a"));

        bool removed = features.Remove(nameof(SampleFeature));

        removed.ShouldBeTrue();
        features.Get(nameof(SampleFeature)).ShouldBeNull();
    }

    [Fact]
    public void Remove_UnknownName_ShouldReturnFalse()
    {
        HttpFeatureCollection features = new();

        features.Remove("not-registered").ShouldBeFalse();
    }

    [Fact]
    public void Version_ShouldIncrementOnMutation()
    {
        HttpFeatureCollection features = new();
        int initial = features.Version;

        features.Set(new SampleFeature("v1"));
        int afterFirstSet = features.Version;

        features.Set(new SampleFeature("v2"));
        int afterSecondSet = features.Version;

        features.Remove(nameof(SampleFeature));
        int afterRemove = features.Version;

        afterFirstSet.ShouldBeGreaterThan(initial);
        afterSecondSet.ShouldBeGreaterThan(afterFirstSet);
        afterRemove.ShouldBeGreaterThan(afterSecondSet);
    }

    [Fact]
    public void Enumerate_ShouldYieldAllRegisteredFeatures()
    {
        HttpFeatureCollection features = new();
        SampleFeature sample = new("s");
        OtherFeature other = new();
        features.Set(sample);
        features.Set(other);

        IHttpFeature[] enumerated = features.ToArray();

        enumerated.Length.ShouldBe(2);
        enumerated.ShouldContain(sample);
        enumerated.ShouldContain(other);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFeatureCollection: Get<T> on the concrete collection allocates nothing, found or missing")]
    public void GetOfT_ConcreteCollectionWithDefaults_ShouldNotAllocate()
    {
        // Arrange — a defaults level under the collection, so the hit is found below a level it must not
        // be hidden by, and the miss walks both levels.
        HttpFeatureCollection defaults = new();
        defaults.Set(new NamedFeature("Defaults.Other"));
        defaults.Set(new SampleFeature("default"));
        HttpFeatureCollection features = new(defaults);
        features.Set(new NamedFeature("Local.Other"));
        _ = features.Get<ISampleFeature>();
        _ = features.Get<IMissingFeature>();

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            _ = features.Get<ISampleFeature>();
            _ = features.Get<IMissingFeature>();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFeatureCollection: Get<T> reads through to a feature installed only in the defaults")]
    public void GetOfT_FeatureOnlyInDefaults_ShouldReturnDefault()
    {
        // Arrange
        HttpFeatureCollection defaults = new();
        SampleFeature fallback = new("default");
        defaults.Set(fallback);
        HttpFeatureCollection features = new(defaults);
        features.Set(new OtherFeature());

        // Act
        ISampleFeature? resolved = features.Get<ISampleFeature>();

        // Assert
        resolved.ShouldBeSameAs(fallback);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFeatureCollection: Get<T> skips a default that a same-named local feature of another type hides")]
    public void GetOfT_LocalOfAnotherTypeShadowsDefault_ShouldReturnNull()
    {
        // Arrange — enumeration never yields the hidden default, so neither may the lookup.
        HttpFeatureCollection defaults = new();
        defaults.Set(new SampleFeature("hidden"));
        HttpFeatureCollection features = new(defaults);
        features.Set(new NamedFeature(nameof(SampleFeature)));

        // Act
        ISampleFeature? resolved = features.Get<ISampleFeature>();

        // Assert
        resolved.ShouldBeNull();
        features.OfType<ISampleFeature>().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFeatureCollection: Get<T> skips a nested default that an intermediate defaults level hides")]
    public void GetOfT_NestedDefaultsHiddenByIntermediateLevel_ShouldReturnFirstVisibleFeature()
    {
        // Arrange — three concrete levels. The intermediate level hides the deepest level's first match
        // by name, so the lookup must check every level above the one it scans, not only the top.
        HttpFeatureCollection deepest = new();
        deepest.Set(new TaggedFeature("A", "deepest-a"));
        TaggedFeature visible = new("B", "deepest-b");
        deepest.Set(visible);
        HttpFeatureCollection intermediate = new(deepest);
        intermediate.Set(new NamedFeature("A"));
        HttpFeatureCollection features = new(intermediate);
        features.Set(new NamedFeature("Top"));

        // Act
        ISampleFeature? resolved = features.Get<ISampleFeature>();

        // Assert
        resolved.ShouldBeSameAs(visible);
        resolved.ShouldBeSameAs(features.OfType<ISampleFeature>().FirstOrDefault());
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFeatureCollection: Get<T> returns a feature from a foreign defaults source that nothing above hides")]
    public void GetOfT_ForeignDefaultsNotShadowed_ShouldReturnForeignFeature()
    {
        // Arrange
        HttpFeatureCollection foreignInner = new();
        TaggedFeature visible = new("Inner", "foreign-inner");
        foreignInner.Set(visible);
        DerivedFeatureCollection foreign = new(foreignInner);
        foreign.Set(new TaggedFeature("Hidden.ByTop", "foreign-hidden"));
        HttpFeatureCollection features = new(foreign);
        features.Set(new NamedFeature("Hidden.ByTop"));

        // Act
        ISampleFeature? resolved = features.Get<ISampleFeature>();

        // Assert
        resolved.ShouldBeSameAs(visible);
        resolved.ShouldBeSameAs(features.OfType<ISampleFeature>().FirstOrDefault());
    }

    private interface IMissingFeature : IHttpFeature
    {
    }

    private sealed class NamedFeature : IOtherFeature
    {
        public NamedFeature(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    private sealed class TaggedFeature : ISampleFeature
    {
        public TaggedFeature(string name, string tag)
        {
            Name = name;
            Tag = tag;
        }

        public string Name { get; }

        public string Tag { get; }
    }

    // Not the exact HttpFeatureCollection type, so the lookup enumerates it rather than scanning it.
    private sealed class DerivedFeatureCollection : HttpFeatureCollection
    {
        public DerivedFeatureCollection()
        {
        }

        public DerivedFeatureCollection(IHttpFeatureCollection defaults)
            : base(defaults)
        {
        }
    }
}
