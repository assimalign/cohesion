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
}
