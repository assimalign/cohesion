using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperMemberMappingTests
{
    private static Mapper CreateMapper(MapperIgnoreHandling ignoreHandling)
    {
        return new MapperBuilder(new MapperOptions { IgnoreHandling = ignoreHandling })
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapMember(target => target.FirstName, source => source.FirstName)
                .MapMember(target => target.MiddleName, source => source.MiddleName)
                .MapMember(target => target.Age, source => source.Age))
            .Build();
    }

    [Fact]
    public void MapMember_PropertyToProperty_CopiesValue()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapMember(target => target.FirstName, source => source.FirstName))
            .Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "Jane" });

        // Assert
        result.FirstName.ShouldBe("Jane");
    }

    [Fact]
    public void MapMember_ValueTypeMember_CopiesValue()
    {
        // Arrange
        var mapper = CreateMapper(MapperIgnoreHandling.Never);

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { Age = 27 });

        // Assert
        result.Age.ShouldBe(27);
    }

    [Fact]
    public void MapMember_ToWiderTargetType_AssignsViaConversion()
    {
        // Arrange: string -> object exercises the converting compiled-setter branch
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapMember(target => target.Tag, source => source.FirstName))
            .Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "tagged" });

        // Assert
        result.Tag.ShouldBe("tagged");
    }

    [Fact]
    public void MapMember_PublicFields_CopiesValue()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile<FieldTarget, FieldSource>(descriptor => descriptor
                .MapMember(target => target.Name, source => source.Name)
                .MapMember(target => target.Value, source => source.Value))
            .Build();

        // Act
        var result = mapper.Map<FieldTarget, FieldSource>(new FieldSource { Name = "f", Value = 9 });

        // Assert
        result.Name.ShouldBe("f");
        result.Value.ShouldBe(9);
    }

    [Fact]
    public void Map_IgnoreHandlingNever_OverwritesTargetWithNullAndDefault()
    {
        // Arrange
        var mapper = CreateMapper(MapperIgnoreHandling.Never);
        var target = new PersonTarget { FirstName = "existing", Age = 50 };

        // Act
        mapper.Map(target, new PersonSource { FirstName = null, Age = 0 }, typeof(PersonTarget), typeof(PersonSource));

        // Assert
        target.FirstName.ShouldBeNull();
        target.Age.ShouldBe(0);
    }

    [Fact]
    public void Map_IgnoreHandlingAlways_KeepsTargetWhenSourceIsNull()
    {
        // Arrange
        var mapper = CreateMapper(MapperIgnoreHandling.Always);
        var target = new PersonTarget { FirstName = "existing" };

        // Act
        mapper.Map(target, new PersonSource { FirstName = null }, typeof(PersonTarget), typeof(PersonSource));

        // Assert
        target.FirstName.ShouldBe("existing");
    }

    [Fact]
    public void Map_IgnoreHandlingAlways_OverwritesWhenSourceIsNotNull()
    {
        // Arrange
        var mapper = CreateMapper(MapperIgnoreHandling.Always);
        var target = new PersonTarget { FirstName = "existing" };

        // Act
        mapper.Map(target, new PersonSource { FirstName = "new" }, typeof(PersonTarget), typeof(PersonSource));

        // Assert
        target.FirstName.ShouldBe("new");
    }

    [Fact]
    public void Map_IgnoreHandlingAlways_WritesValueTypeDefaults()
    {
        // Arrange: value types are never null, so defaults are written under 'Always'
        var mapper = CreateMapper(MapperIgnoreHandling.Always);
        var target = new PersonTarget { Age = 99 };

        // Act
        mapper.Map(target, new PersonSource { Age = 0 }, typeof(PersonTarget), typeof(PersonSource));

        // Assert
        target.Age.ShouldBe(0);
    }

    [Fact]
    public void Map_IgnoreHandlingWhenMappingDefaults_SkipsDefaultValueType()
    {
        // Arrange
        var mapper = CreateMapper(MapperIgnoreHandling.WhenMappingDefaults);
        var target = new PersonTarget { Age = 42 };

        // Act
        mapper.Map(target, new PersonSource { Age = 0 }, typeof(PersonTarget), typeof(PersonSource));

        // Assert
        target.Age.ShouldBe(42);
    }

    [Fact]
    public void Map_IgnoreHandlingWhenMappingDefaults_WritesNonDefaultValueType()
    {
        // Arrange
        var mapper = CreateMapper(MapperIgnoreHandling.WhenMappingDefaults);
        var target = new PersonTarget { Age = 42 };

        // Act
        mapper.Map(target, new PersonSource { Age = 7 }, typeof(PersonTarget), typeof(PersonSource));

        // Assert
        target.Age.ShouldBe(7);
    }

    [Fact]
    public void Map_IgnoreHandlingWhenMappingDefaults_SkipsNullReferenceWithoutThrowing()
    {
        // Arrange: regression — a null source value under WhenMappingDefaults previously threw NullReferenceException
        var mapper = CreateMapper(MapperIgnoreHandling.WhenMappingDefaults);
        var target = new PersonTarget { FirstName = "keep" };

        // Act
        Should.NotThrow(() =>
            mapper.Map(target, new PersonSource { FirstName = null }, typeof(PersonTarget), typeof(PersonSource)));

        // Assert
        target.FirstName.ShouldBe("keep");
    }

    [Fact]
    public void Map_IgnoreHandlingWhenMappingDefaults_WritesNonNullReference()
    {
        // Arrange
        var mapper = CreateMapper(MapperIgnoreHandling.WhenMappingDefaults);
        var target = new PersonTarget { FirstName = "keep" };

        // Act
        mapper.Map(target, new PersonSource { FirstName = "new" }, typeof(PersonTarget), typeof(PersonSource));

        // Assert
        target.FirstName.ShouldBe("new");
    }

    [Fact]
    public void MapMember_ChainedSourceMemberThatIsNull_YieldsDefaultWithoutThrowing()
    {
        // Arrange: source expression dereferences a null chain at map time
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, OrderSource>(descriptor => descriptor
                .MapMember(target => target.FirstName, source => source.Customer!.Name))
            .Build();

        // Act
        PersonTarget result = null!;
        Should.NotThrow(() => result = mapper.Map<PersonTarget, OrderSource>(new OrderSource { Customer = null }));

        // Assert
        result.FirstName.ShouldBeNull();
    }

    [Fact]
    public void MapMember_TargetExpressionNotMember_ThrowsArgumentException()
    {
        // Act / Assert: target body is a method call, not a member access
        Should.Throw<ArgumentException>(() => new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapMember(target => target.FirstName!.ToLower(), source => source.FirstName)));
    }

    [Fact]
    public void MapMember_ChainedTargetMember_CreatesIntermediateAndMaps()
    {
        // Arrange: a chained target member is now supported; the intermediate is created on demand
        var mapper = new MapperBuilder()
            .AddProfile<OrderTarget, OrderSource>(descriptor => descriptor
                .MapMember(target => target.Customer!.Name, source => source.Id))
            .Build();

        // Act
        var result = mapper.Map<OrderTarget, OrderSource>(new OrderSource { Id = "ORD-1" });

        // Assert
        result.Customer.ShouldNotBeNull();
        result.Customer!.Name.ShouldBe("ORD-1");
    }

    [Fact]
    public void MapMember_SourceNotAssignableToTarget_ThrowsInvalidCastException()
    {
        // Act / Assert: string is not assignable to int
        Should.Throw<InvalidCastException>(() => new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapMember(target => target.Age, source => source.FirstName)));
    }

    [Fact]
    public void MapMember_WithNullTargetExpression_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapMember<string, string>(null!, source => source.FirstName!)));
    }
}
