using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperFactoryTests
{
    private static IMapperFactory CreatePeopleFactory()
    {
        return new MapperFactoryBuilder()
            .AddMapper("people", builder => builder
                .AddProfile<PersonTarget, PersonSource>(descriptor =>
                {
                    descriptor.MapMember(target => target.FirstName, source => source.FirstName);
                })
                .Build())
            .Build();
    }

    [Fact]
    public void Create_ResolvesRegisteredMapperByName()
    {
        // Arrange
        var factory = CreatePeopleFactory();

        // Act
        var mapper = factory.Create("people");
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "Sue" });

        // Assert
        mapper.Name.ShouldBe("people");
        result.FirstName.ShouldBe("Sue");
    }

    [Fact]
    public void Create_UnknownName_ThrowsMapperException()
    {
        // Arrange
        var factory = CreatePeopleFactory();

        // Act / Assert
        Should.Throw<MapperException>(() => factory.Create("missing"));
    }

    [Fact]
    public void Create_NullName_ThrowsArgumentNullException()
    {
        // Arrange
        var factory = CreatePeopleFactory();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => factory.Create(null!));
    }

    [Fact]
    public void AddMapper_WithIgnoreHandling_AppliesHandling()
    {
        // Arrange
        var factory = new MapperFactoryBuilder()
            .AddMapper("ignore", MapperIgnoreHandling.Always, builder => builder
                .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                    .MapMember(target => target.FirstName, source => source.FirstName))
                .Build())
            .Build();
        var target = new PersonTarget { FirstName = "existing" };

        // Act: under 'Always', a null source value is not written
        factory.Create("ignore").Map(target, new PersonSource { FirstName = null }, typeof(PersonTarget), typeof(PersonSource));

        // Assert
        target.FirstName.ShouldBe("existing");
    }

    [Fact]
    public void AddMapper_WithCollectionHandling_AppliesHandling()
    {
        // Arrange
        var factory = new MapperFactoryBuilder()
            .AddMapper("collections", MapperCollectionHandling.Merge, builder => builder
                .AddProfile<ListTarget, ItemsSource>(descriptor => descriptor
                    .MapMemberEnumerables(target => target.Items!, source => source.Items!))
                .AddProfile<LineItemTarget, LineItemSource>(descriptor => descriptor
                    .MapMember(target => target.Sku, source => source.Sku))
                .Build())
            .Build();
        var target = new ListTarget { Items = new List<LineItemTarget> { new() { Sku = "OLD" } } };

        // Act
        factory.Create("collections").Map(
            target,
            new ItemsSource { Items = new List<LineItemSource> { new() { Sku = "A" } } },
            typeof(ListTarget),
            typeof(ItemsSource));

        // Assert: merge keeps the existing element and appends the mapped one
        target.Items!.Count.ShouldBe(2);
    }

    [Fact]
    public void AddMapper_WithIgnoreAndCollectionHandling_RegistersMapper()
    {
        // Arrange / Act
        var factory = new MapperFactoryBuilder()
            .AddMapper("both", MapperIgnoreHandling.Always, MapperCollectionHandling.Merge, builder => builder
                .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                    .MapMember(target => target.FirstName, source => source.FirstName))
                .Build())
            .Build();

        // Assert
        factory.Create("both").Name.ShouldBe("both");
    }

    [Fact]
    public void AddMapper_NullName_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new MapperFactoryBuilder()
            .AddMapper(null!, builder => builder.Build()));
    }

    [Fact]
    public void AddMapper_NullConfigure_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new MapperFactoryBuilder()
            .AddMapper("x", (Func<MapperBuilder, Mapper>)null!));
    }

    [Fact]
    public void AddMapper_PreBuiltMapper_IsKeyedByName()
    {
        // Arrange
        var mapper = new MapperBuilder(new MapperOptions { Name = "prebuilt" }).Build();
        IMapperFactoryBuilder builder = new MapperFactoryBuilder();

        // Act
        builder.AddMapper(mapper);
        var factory = builder.Build();

        // Assert
        factory.Create("prebuilt").ShouldBeSameAs(mapper);
    }

    [Fact]
    public void AddMapper_FactoryCallback_IsKeyedByMapperName()
    {
        // Arrange
        IMapperFactoryBuilder builder = new MapperFactoryBuilder();

        // Act
        builder.AddMapper(b => ((MapperBuilder)b)
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapMember(target => target.FirstName, source => source.FirstName))
            .Build());
        var factory = builder.Build();

        // Assert: default builder name is "Default"
        factory.Create("Default").ShouldNotBeNull();
    }

    [Fact]
    public void AddMapper_NullMapperInstance_ThrowsArgumentNullException()
    {
        // Arrange
        IMapperFactoryBuilder builder = new MapperFactoryBuilder();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddMapper((IMapper)null!));
    }

    [Fact]
    public void AddMapper_NullFactoryCallback_ThrowsArgumentNullException()
    {
        // Arrange
        IMapperFactoryBuilder builder = new MapperFactoryBuilder();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddMapper((Func<IMapperBuilder, IMapper>)null!));
    }
}
