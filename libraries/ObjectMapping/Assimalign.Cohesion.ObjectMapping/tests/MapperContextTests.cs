using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperContextTests
{
    private static MapperContext Create(object target, object source)
    {
        return new MapperContext(target, source) { Profiles = new List<IMapperProfile>() };
    }

    [Fact]
    public void Constructor_NullTarget_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() =>
            new MapperContext(null!, new PersonSource()) { Profiles = new List<IMapperProfile>() });
    }

    [Fact]
    public void Constructor_NullSource_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() =>
            new MapperContext(new PersonTarget(), null!) { Profiles = new List<IMapperProfile>() });
    }

    [Fact]
    public void Properties_ReflectConstructorAndInitializers()
    {
        // Arrange
        var target = new PersonTarget();
        var source = new PersonSource();
        var profiles = new List<IMapperProfile>();

        // Act
        var context = new MapperContext(target, source)
        {
            Profiles = profiles,
            IgnoreHandling = MapperIgnoreHandling.Always,
            CollectionHandling = MapperCollectionHandling.Merge
        };

        // Assert
        context.Target.ShouldBeSameAs(target);
        context.Source.ShouldBeSameAs(source);
        context.Profiles.ShouldBeSameAs(profiles);
        context.IgnoreHandling.ShouldBe(MapperIgnoreHandling.Always);
        context.CollectionHandling.ShouldBe(MapperCollectionHandling.Merge);
    }

    [Fact]
    public void IgnoreHandling_WhenNotSet_DefaultsToNever()
    {
        // Arrange
        var context = Create(new PersonTarget(), new PersonSource());

        // Act / Assert
        context.IgnoreHandling.ShouldBe(MapperIgnoreHandling.Never);
    }

    [Fact]
    public void CollectionHandling_WhenNotSet_DefaultsToOverride()
    {
        // Arrange
        var context = Create(new PersonTarget(), new PersonSource());

        // Act / Assert
        context.CollectionHandling.ShouldBe(MapperCollectionHandling.Override);
    }
}
