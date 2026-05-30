using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperEnumerableMappingTests
{
    private static MapperBuilder WithLineItemProfile(MapperBuilder builder)
    {
        return builder.AddProfile<LineItemTarget, LineItemSource>(descriptor => descriptor
            .MapMember(target => target.Sku, source => source.Sku)
            .MapMember(target => target.Quantity, source => source.Quantity));
    }

    private static ItemsSource SampleSource()
    {
        return new ItemsSource
        {
            Items = new List<LineItemSource>
            {
                new() { Sku = "A", Quantity = 1 },
                new() { Sku = "B", Quantity = 2 }
            }
        };
    }

    [Fact]
    public void MapMemberEnumerables_IntoList_MapsEachElement()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder()
            .AddProfile<ListTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();

        // Act
        var result = mapper.Map<ListTarget, ItemsSource>(SampleSource());

        // Assert
        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(2);
        result.Items[0].Sku.ShouldBe("A");
        result.Items[0].Quantity.ShouldBe(1);
        result.Items[1].Sku.ShouldBe("B");
        result.Items[1].Quantity.ShouldBe(2);
    }

    [Fact]
    public void MapMemberEnumerables_IntoArray_MapsEachElement()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder()
            .AddProfile<ArrayTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();

        // Act
        var result = mapper.Map<ArrayTarget, ItemsSource>(SampleSource());

        // Assert
        result.Items.ShouldNotBeNull();
        result.Items!.Length.ShouldBe(2);
        result.Items[0].Sku.ShouldBe("A");
        result.Items[1].Sku.ShouldBe("B");
    }

    [Fact]
    public void MapMemberEnumerables_IntoIEnumerable_MapsEachElement()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder()
            .AddProfile<EnumerableTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();

        // Act
        var result = mapper.Map<EnumerableTarget, ItemsSource>(SampleSource());

        // Assert
        result.Items.ShouldNotBeNull();
        result.Items!.Count().ShouldBe(2);
    }

    [Fact]
    public void MapMemberEnumerables_IntoIList_MapsEachElement()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder()
            .AddProfile<IListTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();

        // Act
        var result = mapper.Map<IListTarget, ItemsSource>(SampleSource());

        // Assert
        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(2);
    }

    [Fact]
    public void MapMemberEnumerables_IntoHashSet_MapsEachElement()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder()
            .AddProfile<SetTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();

        // Act
        var result = mapper.Map<SetTarget, ItemsSource>(SampleSource());

        // Assert
        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(2);
    }

    [Fact]
    public void MapMemberEnumerables_IntoQueue_MapsEachElement()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder()
            .AddProfile<QueueTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();

        // Act
        var result = mapper.Map<QueueTarget, ItemsSource>(SampleSource());

        // Assert
        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(2);
        result.Items.Peek().Sku.ShouldBe("A");
    }

    [Fact]
    public void MapMemberEnumerables_IntoStack_MapsEachElement()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder()
            .AddProfile<StackTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();

        // Act
        var result = mapper.Map<StackTarget, ItemsSource>(SampleSource());

        // Assert
        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(2);
    }

    [Fact]
    public void MapMemberEnumerables_OverrideHandling_ReplacesExisting()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder(new MapperOptions { CollectionHandling = MapperCollectionHandling.Override })
            .AddProfile<ListTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();
        var target = new ListTarget { Items = new List<LineItemTarget> { new() { Sku = "OLD" } } };

        // Act
        mapper.Map(target, SampleSource(), typeof(ListTarget), typeof(ItemsSource));

        // Assert
        target.Items!.Count.ShouldBe(2);
        target.Items.ShouldNotContain(item => item.Sku == "OLD");
    }

    [Fact]
    public void MapMemberEnumerables_MergeHandling_PreservesExistingThenAppendsMapped()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder(new MapperOptions { CollectionHandling = MapperCollectionHandling.Merge })
            .AddProfile<ListTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();
        var target = new ListTarget { Items = new List<LineItemTarget> { new() { Sku = "OLD" } } };

        // Act
        mapper.Map(target, SampleSource(), typeof(ListTarget), typeof(ItemsSource));

        // Assert
        target.Items!.Count.ShouldBe(3);
        target.Items[0].Sku.ShouldBe("OLD");
        target.Items[1].Sku.ShouldBe("A");
        target.Items[2].Sku.ShouldBe("B");
    }

    [Fact]
    public void MapMemberEnumerables_NullSourceCollection_LeavesTargetUnchanged()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder()
            .AddProfile<ListTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();
        var existing = new List<LineItemTarget> { new() { Sku = "keep" } };
        var target = new ListTarget { Items = existing };

        // Act
        mapper.Map(target, new ItemsSource { Items = null }, typeof(ListTarget), typeof(ItemsSource));

        // Assert
        target.Items.ShouldBeSameAs(existing);
    }

    [Fact]
    public void MapMemberEnumerables_EmptySourceCollection_ProducesEmptyTarget()
    {
        // Arrange
        var mapper = WithLineItemProfile(new MapperBuilder()
            .AddProfile<ListTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => target.Items!, source => source.Items!)))
            .Build();

        // Act
        var result = mapper.Map<ListTarget, ItemsSource>(new ItemsSource { Items = new List<LineItemSource>() });

        // Assert
        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(0);
    }

    [Fact]
    public void MapMemberEnumerables_TargetExpressionNotMember_ThrowsArgumentException()
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => new MapperBuilder()
            .AddProfile<ListTarget, ItemsSource>(descriptor => descriptor
                .MapMemberEnumerables(target => new List<LineItemTarget>(), source => source.Items!)));
    }
}
