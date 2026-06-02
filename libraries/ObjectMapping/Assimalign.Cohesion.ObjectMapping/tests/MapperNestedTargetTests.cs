using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperNestedTargetTests
{
    // Non-partial, so the generator skips it and Configure runs at run time (the expression path).
    private sealed class Test3ExpressionProfile : MapperProfile<Test3, Test1>
    {
        protected override void Configure(MapperProfileDescriptor<Test3, Test1> descriptor)
        {
            descriptor.MapMember(target => target.Info!.FirstName, source => source.FirstName);
        }
    }

    private sealed class NoCtorProfile : MapperProfile<NoCtorTarget, NoCtorSource>
    {
        protected override void Configure(MapperProfileDescriptor<NoCtorTarget, NoCtorSource> descriptor)
        {
            descriptor.MapMember(target => target.Nested!.Name, source => source.Name);
        }
    }

    [Fact]
    public void ExpressionPath_ChainedTarget_CreatesIntermediateAndMaps()
    {
        // Arrange
        var mapper = new MapperBuilder().AddProfile(new Test3ExpressionProfile()).Build();

        // Act
        var result = mapper.Map<Test3, Test1>(new Test1 { FirstName = "John" });

        // Assert
        result.Info.ShouldNotBeNull();
        result.Info!.FirstName.ShouldBe("John");
    }

    [Fact]
    public void GeneratedPath_ChainedTarget_CreatesIntermediateAndMaps()
    {
        // Arrange / Act: the inline call is intercepted, so the registered profile is generated.
        var mapper = new MapperBuilder()
            .AddProfile<Test3, Test1>(descriptor => descriptor
                .MapMember(target => target.Info!.FirstName, source => source.FirstName))
            .Build();

        // Assert
        mapper.Profiles[0].GetType().IsGenericType.ShouldBeFalse();

        var result = mapper.Map<Test3, Test1>(new Test1 { FirstName = "Jane" });
        result.Info.ShouldNotBeNull();
        result.Info!.FirstName.ShouldBe("Jane");
    }

    [Fact]
    public void GeneratedPath_BlockBodiedInline_ChainedTarget_IsInterceptedAndMaps()
    {
        // Arrange / Act: mirrors the block-bodied inline form `descriptor => { descriptor.MapMember(...); }`.
        var mapper = new MapperBuilder()
            .AddProfile<Test3, Test1>(descriptor =>
            {
                descriptor.MapMember(target => target.Info!.FirstName, source => source.FirstName);
            })
            .Build();

        // Assert
        mapper.Profiles[0].GetType().IsGenericType.ShouldBeFalse();

        var result = mapper.Map<Test3, Test1>(new Test1 { FirstName = "Block" });
        result.Info.ShouldNotBeNull();
        result.Info!.FirstName.ShouldBe("Block");
    }

    [Fact]
    public void ChainedTarget_ReusesExistingIntermediate()
    {
        // Arrange
        var mapper = new MapperBuilder().AddProfile(new Test3ExpressionProfile()).Build();
        var existing = new Test3Info { LastName = "Doe" };
        var target = new Test3 { Info = existing };

        // Act
        mapper.Map(target, new Test1 { FirstName = "John" });

        // Assert
        target.Info.ShouldBeSameAs(existing);    // existing intermediate is not recreated
        target.Info!.FirstName.ShouldBe("John"); // leaf written
        target.Info.LastName.ShouldBe("Doe");    // existing data preserved
    }

    [Fact]
    public void ChainedTarget_IntermediateWithoutParameterlessConstructor_ThrowsMapperException()
    {
        // Act / Assert: the intermediate cannot be created on demand
        Should.Throw<MapperException>(() => new MapperBuilder().AddProfile(new NoCtorProfile()).Build());
    }
}
