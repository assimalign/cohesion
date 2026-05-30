using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperGeneratedPathTests
{
    // Non-partial so the generator skips them; these hand-write TryConfigureGenerated to validate
    // the delegate-based nested + enumerable runtime paths (the shape the generator emits).
    private sealed class DelegateOrderProfile : MapperProfile<OrderTarget, OrderSource>
    {
        protected override void Configure(MapperProfileDescriptor<OrderTarget, OrderSource> descriptor)
            => throw new InvalidOperationException("Reflection-based Configure should not run.");

        protected override bool TryConfigureGenerated(MapperProfileDescriptor<OrderTarget, OrderSource> descriptor)
        {
            descriptor
                .MapMember(static source => source.Id, static (target, value) => target.Id = value)
                .MapMemberTypes(static source => source.Customer, static target => target.Customer, static (target, value) => target.Customer = value)
                .MapMemberEnumerables(static source => source.Items, static target => target.Items, static (target, items) => target.Items = items.ToList());
            return true;
        }
    }

    private sealed class DelegateCustomerProfile : MapperProfile<CustomerTarget, CustomerSource>
    {
        protected override void Configure(MapperProfileDescriptor<CustomerTarget, CustomerSource> descriptor)
            => throw new InvalidOperationException("Reflection-based Configure should not run.");

        protected override bool TryConfigureGenerated(MapperProfileDescriptor<CustomerTarget, CustomerSource> descriptor)
        {
            descriptor
                .MapMember(static source => source.Name, static (target, value) => target.Name = value)
                .MapMember(static source => source.Email, static (target, value) => target.Email = value);
            return true;
        }
    }

    private sealed class DelegateLineItemProfile : MapperProfile<LineItemTarget, LineItemSource>
    {
        protected override void Configure(MapperProfileDescriptor<LineItemTarget, LineItemSource> descriptor)
            => throw new InvalidOperationException("Reflection-based Configure should not run.");

        protected override bool TryConfigureGenerated(MapperProfileDescriptor<LineItemTarget, LineItemSource> descriptor)
        {
            descriptor
                .MapMember(static source => source.Sku, static (target, value) => target.Sku = value)
                .MapMember(static source => source.Quantity, static (target, value) => target.Quantity = value);
            return true;
        }
    }

    [Fact]
    public void DelegateNestedAndEnumerable_MapWithoutExpressionCompilation()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile(new DelegateOrderProfile())
            .AddProfile(new DelegateCustomerProfile())
            .AddProfile(new DelegateLineItemProfile())
            .Build();

        // Act
        var result = mapper.Map<OrderTarget, OrderSource>(new OrderSource
        {
            Id = "O1",
            Customer = new CustomerSource { Name = "Acme", Email = "a@acme.test" },
            Items = new List<LineItemSource>
            {
                new() { Sku = "A", Quantity = 1 },
                new() { Sku = "B", Quantity = 2 }
            }
        });

        // Assert
        result.Id.ShouldBe("O1");
        result.Customer.ShouldNotBeNull();
        result.Customer!.Name.ShouldBe("Acme");
        result.Customer.Email.ShouldBe("a@acme.test");
        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(2);
        result.Items[0].Sku.ShouldBe("A");
        result.Items[1].Quantity.ShouldBe(2);
    }

    [Fact]
    public void Generator_EmitsTryConfigureGeneratedOverrideOnProfile()
    {
        // The source generator should have emitted a TryConfigureGenerated override on the
        // partial profile type. If it did not run, the method is only declared on the base class.
        var method = typeof(GeneratedScalarProfile).GetMethod(
            "TryConfigureGenerated",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method.ShouldNotBeNull();
        method!.DeclaringType.ShouldBe(typeof(GeneratedScalarProfile));
    }

    [Fact]
    public void Generator_GeneratedProfileMapsCorrectly()
    {
        // Arrange
        var mapper = new MapperBuilder().AddProfile(new GeneratedScalarProfile()).Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(
            new PersonSource { FirstName = "Gen", LastName = "Erated", Age = 9 });

        // Assert
        result.FirstName.ShouldBe("Gen");
        result.LastName.ShouldBe("Erated");
        result.Age.ShouldBe(9);
    }

    [Fact]
    public void Generator_EmitsForNestedAndEnumerableProfile()
    {
        // The generator should cover a profile combining scalar, nested, and enumerable mappings.
        var method = typeof(GeneratedOrderProfile).GetMethod(
            "TryConfigureGenerated",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method.ShouldNotBeNull();
        method!.DeclaringType.ShouldBe(typeof(GeneratedOrderProfile));
    }

    [Fact]
    public void Generator_NestedAndEnumerableProfile_MapsCorrectly()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile(new GeneratedOrderProfile())
            .AddProfile(new GeneratedCustomerProfile())
            .AddProfile(new GeneratedLineItemProfile())
            .Build();

        // Act
        var result = mapper.Map<OrderTarget, OrderSource>(new OrderSource
        {
            Id = "O9",
            Customer = new CustomerSource { Name = "Globex", Email = "g@x.test" },
            Items = new List<LineItemSource>
            {
                new() { Sku = "X", Quantity = 4 },
                new() { Sku = "Y", Quantity = 5 }
            }
        });

        // Assert
        result.Id.ShouldBe("O9");
        result.Customer.ShouldNotBeNull();
        result.Customer!.Name.ShouldBe("Globex");
        result.Customer.Email.ShouldBe("g@x.test");
        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(2);
        result.Items[1].Sku.ShouldBe("Y");
        result.Items[1].Quantity.ShouldBe(5);
    }

    [Fact]
    public void Generator_InterceptsInlineAddProfile()
    {
        // Arrange / Act
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(d => d
                .MapMember(t => t.FirstName, s => s.FirstName)
                .MapMember(t => t.Age, s => s.Age))
            .Build();

        // Assert: the inline call was intercepted, so the registered profile is a generated
        // (non-generic) class rather than the generic DefaultMapperProfile<,> fallback.
        mapper.Profiles[0].GetType().IsGenericType.ShouldBeFalse();

        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "Inline", Age = 3 });
        result.FirstName.ShouldBe("Inline");
        result.Age.ShouldBe(3);
    }

    // Mirrors exactly what the object-mapping source generator emits: a partial profile that
    // supplies delegate-based mappings via TryConfigureGenerated, bypassing the
    // expression-compiling Configure path entirely (no Expression.Compile -> AOT safe).
    private sealed class GeneratedStylePersonProfile : MapperProfile<PersonTarget, PersonSource>
    {
        protected override void Configure(MapperProfileDescriptor<PersonTarget, PersonSource> descriptor)
        {
            // The generated path must win; if this runs, the test fails loudly.
            throw new InvalidOperationException("Reflection-based Configure should not run when generated configuration is present.");
        }

        protected override bool TryConfigureGenerated(MapperProfileDescriptor<PersonTarget, PersonSource> descriptor)
        {
            descriptor
                .MapMember(static source => source.FirstName, static (target, value) => target.FirstName = value)
                .MapMember(static source => source.Age, static (target, value) => target.Age = value);
            return true;
        }
    }

    [Fact]
    public void GeneratedConfiguration_IsPreferredOverReflectionConfigure()
    {
        // Arrange: constructing the profile must not invoke the throwing Configure
        var mapper = new MapperBuilder().AddProfile(new GeneratedStylePersonProfile()).Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "Gen", Age = 7 });

        // Assert
        result.FirstName.ShouldBe("Gen");
        result.Age.ShouldBe(7);
    }

    [Fact]
    public void DelegateMapMember_MapsWithoutExpressionCompilation()
    {
        // Arrange: the AOT-safe delegate form, registered via an inline profile
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapMember(static source => source.LastName, static (target, value) => target.LastName = value))
            .Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { LastName = "NoCompile" });

        // Assert
        result.LastName.ShouldBe("NoCompile");
    }
}
