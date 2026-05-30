using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperNestedMappingTests
{
    private static Mapper CreateOrderMapper(MapperIgnoreHandling ignoreHandling = MapperIgnoreHandling.Never)
    {
        return new MapperBuilder(new MapperOptions { IgnoreHandling = ignoreHandling })
            .AddProfile<OrderTarget, OrderSource>(descriptor => descriptor
                .MapMember(target => target.Id, source => source.Id)
                .MapMemberTypes(target => target.Customer!, source => source.Customer!))
            .AddProfile<CustomerTarget, CustomerSource>(descriptor => descriptor
                .MapMember(target => target.Name, source => source.Name)
                .MapMember(target => target.Email, source => source.Email))
            .Build();
    }

    [Fact]
    public void MapMemberTypes_PopulatesNestedMemberFromRegisteredProfile()
    {
        // Arrange
        var mapper = CreateOrderMapper();
        var source = new OrderSource
        {
            Id = "O-1",
            Customer = new CustomerSource { Name = "Acme", Email = "a@acme.test" }
        };

        // Act
        var result = mapper.Map<OrderTarget, OrderSource>(source);

        // Assert
        result.Id.ShouldBe("O-1");
        result.Customer.ShouldNotBeNull();
        result.Customer!.Name.ShouldBe("Acme");
        result.Customer.Email.ShouldBe("a@acme.test");
    }

    [Fact]
    public void MapMemberTypes_ExistingTargetMember_IsMappedOntoInPlace()
    {
        // Arrange
        var mapper = CreateOrderMapper();
        var existing = new CustomerTarget { Name = "old", Email = "old@test" };
        var target = new OrderTarget { Customer = existing };

        // Act
        mapper.Map(
            target,
            new OrderSource { Customer = new CustomerSource { Name = "new", Email = "new@test" } },
            typeof(OrderTarget),
            typeof(OrderSource));

        // Assert
        target.Customer.ShouldBeSameAs(existing);
        existing.Name.ShouldBe("new");
        existing.Email.ShouldBe("new@test");
    }

    [Fact]
    public void MapMemberTypes_NullNestedSourceUnderNever_SetsTargetMemberToNull()
    {
        // Arrange
        var mapper = CreateOrderMapper(MapperIgnoreHandling.Never);
        var target = new OrderTarget { Customer = new CustomerTarget { Name = "old" } };

        // Act
        mapper.Map(target, new OrderSource { Id = "O", Customer = null }, typeof(OrderTarget), typeof(OrderSource));

        // Assert
        target.Customer.ShouldBeNull();
    }

    [Fact]
    public void MapMemberTypes_NullNestedSourceUnderAlways_LeavesTargetMember()
    {
        // Arrange
        var mapper = CreateOrderMapper(MapperIgnoreHandling.Always);
        var existing = new CustomerTarget { Name = "old" };
        var target = new OrderTarget { Customer = existing };

        // Act
        mapper.Map(target, new OrderSource { Id = "O", Customer = null }, typeof(OrderTarget), typeof(OrderSource));

        // Assert
        target.Customer.ShouldBeSameAs(existing);
    }

    [Fact]
    public void MapMemberTypes_NoElementProfileRegistered_CreatesEmptyNonNullMember()
    {
        // Arrange: no CustomerTarget/CustomerSource profile is registered
        var mapper = new MapperBuilder()
            .AddProfile<OrderTarget, OrderSource>(descriptor => descriptor
                .MapMemberTypes(target => target.Customer!, source => source.Customer!))
            .Build();

        // Act
        var result = mapper.Map<OrderTarget, OrderSource>(new OrderSource { Customer = new CustomerSource { Name = "X" } });

        // Assert: the member is created but left unmapped
        result.Customer.ShouldNotBeNull();
        result.Customer!.Name.ShouldBeNull();
    }

    [Fact]
    public void MapMemberTypes_TargetExpressionNotMember_ThrowsArgumentException()
    {
        // Act / Assert: target expression must select a member, not construct a value
        Should.Throw<ArgumentException>(() => new MapperBuilder()
            .AddProfile<OrderTarget, OrderSource>(descriptor => descriptor
                .MapMemberTypes(target => new CustomerTarget(), source => source.Customer!)));
    }
}
