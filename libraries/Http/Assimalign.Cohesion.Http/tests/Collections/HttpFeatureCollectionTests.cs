using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpFeatureCollectionTests
{
    private interface ISampleFeature
    {
        string Tag { get; }
    }

    private sealed class SampleFeature : ISampleFeature
    {
        public SampleFeature(string tag)
        {
            Tag = tag;
        }

        public string Tag { get; }
    }

    private interface IOtherFeature
    {
    }

    private sealed class OtherFeature : IOtherFeature
    {
    }

    [Fact]
    public void Get_NotRegistered_ShouldReturnNull()
    {
        // Arrange
        HttpFeatureCollection features = new();

        // Act
        ISampleFeature? feature = features.Get<ISampleFeature>();

        // Assert
        feature.ShouldBeNull();
    }

    [Fact]
    public void SetThenGet_ShouldReturnSameInstance()
    {
        // Arrange
        HttpFeatureCollection features = new();
        SampleFeature instance = new("alpha");

        // Act
        features.Set<ISampleFeature>(instance);
        ISampleFeature? retrieved = features.Get<ISampleFeature>();

        // Assert
        retrieved.ShouldBeSameAs(instance);
        retrieved!.Tag.ShouldBe("alpha");
    }

    [Fact]
    public void Set_NullInstance_ShouldRemoveExistingRegistration()
    {
        // Arrange
        HttpFeatureCollection features = new();
        features.Set<ISampleFeature>(new SampleFeature("a"));

        // Act
        features.Set<ISampleFeature>(null);

        // Assert
        features.Get<ISampleFeature>().ShouldBeNull();
    }

    [Fact]
    public void Set_TwiceWithDifferentInstances_ShouldReturnLatest()
    {
        // Arrange
        HttpFeatureCollection features = new();
        features.Set<ISampleFeature>(new SampleFeature("first"));
        SampleFeature second = new("second");

        // Act
        features.Set<ISampleFeature>(second);

        // Assert
        features.Get<ISampleFeature>().ShouldBeSameAs(second);
    }

    [Fact]
    public void DifferentFeatureTypes_ShouldCoexistIndependently()
    {
        // Arrange
        HttpFeatureCollection features = new();
        SampleFeature sample = new("s");
        OtherFeature other = new();

        // Act
        features.Set<ISampleFeature>(sample);
        features.Set<IOtherFeature>(other);

        // Assert
        features.Get<ISampleFeature>().ShouldBeSameAs(sample);
        features.Get<IOtherFeature>().ShouldBeSameAs(other);
    }

    [Fact]
    public void Get_FeatureRegisteredAsInterface_ShouldNotBeRetrievableAsConcreteType()
    {
        // The collection is keyed strictly by the type-parameter; registering
        // as the interface and looking up as the concrete class returns null
        // because they are different Type objects.
        // Arrange
        HttpFeatureCollection features = new();
        SampleFeature concrete = new("a");
        features.Set<ISampleFeature>(concrete);

        // Act
        SampleFeature? viaConcrete = features.Get<SampleFeature>();
        ISampleFeature? viaInterface = features.Get<ISampleFeature>();

        // Assert
        viaConcrete.ShouldBeNull();
        viaInterface.ShouldBeSameAs(concrete);
    }
}
