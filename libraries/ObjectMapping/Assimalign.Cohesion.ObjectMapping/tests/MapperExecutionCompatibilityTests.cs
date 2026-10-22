using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperExecutionCompatibilityTests
{
    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Sequence helpers retain custom enumeration on derived lists")]
    public void Map_SequenceWithDerivedSourceList_PreservesCustomOrder()
    {
        // Arrange
        var mapper = new MapperBuilder().AddProfile<ExecutionTarget, ExecutionSource>(d => d
            .MapMember(static s => s.Value, static (t, value) => t.Value = value)).Build();
        IEnumerable<ExecutionSource> sources = new ExecutionReversedList
        {
            new ExecutionSource { Value = 1 }, new ExecutionSource { Value = 2 }
        };

        // Act
        var result = mapper.Map<ExecutionTarget, ExecutionSource>(sources);

        // Assert
        result.Select(static item => item.Value).ShouldBe(new[] { 2, 1 });
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Derived lists retain their custom interface enumeration")]
    public void Map_WithDerivedSourceList_PreservesCustomOrder()
    {
        // Arrange
        var mapper = new MapperBuilder().AddProfile<ExecutionTarget, ExecutionSource>(d => d
            .MapMember(static s => s.Value, static (t, value) => t.Value = value)
            .MapMemberEnumerables(static s => s.Items, static t => t.Items, static (t, items) => t.Items = items.ToList())).Build();
        var source = new ExecutionSource { Items = new ExecutionReversedList
        {
            new ExecutionSource { Value = 1 }, new ExecutionSource { Value = 2 }
        } };

        // Act
        var result = mapper.Map<ExecutionTarget, ExecutionSource>(source);

        // Assert
        result.Items.Select(static item => item.Value).ShouldBe(new[] { 2, 1 });
    }

    [Theory(DisplayName = "Cohesion Test [ObjectMapping] - Generic helpers preserve custom mapper dispatch and results")]
    [InlineData(false)]
    [InlineData(true)]
    public void Map_WithCustomMapper_UsesPrimitiveDispatch(bool existing)
    {
        // Arrange
        var mapper = new ExecutionCustomMapper();
        var source = new ExecutionSource();
        var target = new ExecutionTarget();

        // Act
        var result = existing
            ? mapper.Map<ExecutionTarget, ExecutionSource>(target, source)
            : mapper.Map<ExecutionTarget, ExecutionSource>(source);

        // Assert
        result.ShouldBeSameAs(mapper.Result);
        mapper.LastSource.ShouldBeSameAs(source);
        mapper.LastTarget.ShouldBeOfType<ExecutionTarget>();
        mapper.LastTargetType.ShouldBe(typeof(ExecutionTarget));
        mapper.LastSourceType.ShouldBe(typeof(ExecutionSource));
        if (existing)
        {
            mapper.LastTarget.ShouldBeSameAs(target);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Later actions see changes to a boxed value source")]
    public void Map_WhenCallbackChangesBoxedSource_LaterMembersReadUpdatedValue()
    {
        // Arrange
        var mapper = new MapperBuilder().AddProfile<ExecutionTarget, ExecutionValueSource>(d => d
            .MapAction(static c => Unsafe.Unbox<ExecutionValueSource>(c.Source).Value = 42)
            .MapMember(static s => s.Value, static (t, value) => t.Value = value)).Build();

        // Act
        var target = mapper.Map<ExecutionTarget, ExecutionValueSource>(new ExecutionValueSource { Value = 1 });

        // Assert
        target.Value.ShouldBe(42);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Generic mapping returns the updated boxed value target")]
    public void Map_WhenCallbackChangesBoxedTarget_ReturnsUpdatedBox()
    {
        // Arrange
        var mapper = new MapperBuilder().AddProfile<ExecutionValueTarget, ExecutionSource>(d => d
            .MapAction(static c => Unsafe.Unbox<ExecutionValueTarget>(c.Target).Value = 42)).Build();

        // Act
        var target = mapper.Map<ExecutionValueTarget, ExecutionSource>(new ExecutionSource());

        // Assert
        target.Value.ShouldBe(42);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Custom actions share one stable context per invocation")]
    public void Map_WithCustomAndBuiltInProfiles_PreservesContextIdentity()
    {
        // Arrange
        var contexts = new List<IMapperContext>();
        var options = new MapperOptions();
        var mapper = new MapperBuilder(options)
            .AddProfile<ExecutionTarget, ExecutionSource>(d => d
                .MapMember(static s => s.Value, static (t, value) => t.Value = value)
                .MapAction(contexts.Add))
            .AddProfile(new ExecutionCustomProfile { MapActions = new[] { new ExecutionCallback(contexts.Add) } })
            .Build();
        var firstSource = new ExecutionSource { Value = 4 };
        var secondSource = new ExecutionSource { Value = 8 };

        // Act
        var first = mapper.Map<ExecutionTarget, ExecutionSource>(firstSource);
        var second = mapper.Map<ExecutionTarget, ExecutionSource>(secondSource);

        // Assert
        contexts.Count.ShouldBe(4);
        contexts[0].ShouldBeOfType<MapperContext>();
        contexts[0].ShouldBeSameAs(contexts[1]);
        contexts[2].ShouldBeSameAs(contexts[3]);
        ReferenceEquals(contexts[0], contexts[2]).ShouldBeFalse();
        contexts[0].Source.ShouldBeSameAs(firstSource);
        contexts[0].Target.ShouldBeSameAs(first);
        contexts[2].Source.ShouldBeSameAs(secondSource);
        contexts[2].Target.ShouldBeSameAs(second);
        contexts[0].Profiles.ShouldBeSameAs(options.Profiles);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Nested callbacks retain independent contexts after mapping")]
    public void Map_WithNestedAndCollectionCallbacks_PreservesCapturedContexts()
    {
        // Arrange
        var contexts = new List<IMapperContext>();
        var mapper = new MapperBuilder()
            .AddProfile<ExecutionTarget, ExecutionSource>(d => d
                .MapMember(static s => s.Value, static (t, value) => t.Value = value)
                .MapMemberTypes(static s => s.Child, static t => t.Child, static (t, value) => t.Child = value)
                .MapMemberEnumerables(static s => s.Items, static t => t.Items, static (t, items) => t.Items = items.ToList())
                .MapAction(contexts.Add))
            .Build();
        var source = new ExecutionSource { Value = 1, Child = new ExecutionSource { Value = 2 },
            Items = new List<ExecutionSource> { new ExecutionSource { Value = 3 }, new ExecutionSource { Value = 4 } } };

        // Act
        var target = mapper.Map<ExecutionTarget, ExecutionSource>(source);

        // Assert
        contexts.Count.ShouldBe(4);
        contexts.Distinct().Count().ShouldBe(4);
        contexts[0].Source.ShouldBeSameAs(source.Child);
        contexts[0].Target.ShouldBeSameAs(target.Child);
        contexts[1].Source.ShouldBeSameAs(source.Items[0]);
        contexts[1].Target.ShouldBeSameAs(target.Items[0]);
        contexts[2].Source.ShouldBeSameAs(source.Items[1]);
        contexts[2].Target.ShouldBeSameAs(target.Items[1]);
        contexts[3].Target.ShouldBeSameAs(target);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Mapping observes profile additions and snapshots handling flags")]
    public void Map_WhenCallbackChangesOptions_PreservesLiveProfilesAndInvocationFlags()
    {
        // Arrange
        var options = new MapperOptions();
        var contexts = new List<IMapperContext>();
        var late = new ExecutionCustomProfile { MapActions = new[] { new ExecutionCallback(contexts.Add) } };
        var mapper = new MapperBuilder(options).AddProfile<ExecutionTarget, ExecutionSource>(d => d.MapAction(context =>
        {
            options.IgnoreHandling = MapperIgnoreHandling.Always;
            options.CollectionHandling = MapperCollectionHandling.Merge;
            options.Profiles.Add(late);
        })).Build();

        // Act
        mapper.Map<ExecutionTarget, ExecutionSource>(new ExecutionSource());

        // Assert
        contexts.Count.ShouldBe(1);
        contexts[0].IgnoreHandling.ShouldBe(MapperIgnoreHandling.Never);
        contexts[0].CollectionHandling.ShouldBe(MapperCollectionHandling.Override);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Mapping still rejects actions mutated during enumeration")]
    public void Map_WhenCallbackChangesCurrentActions_ThrowsInvalidOperationException()
    {
        // Arrange
        MapperProfileDescriptor<ExecutionTarget, ExecutionSource>? descriptor = null;
        var mapper = new MapperBuilder().AddProfile<ExecutionTarget, ExecutionSource>(d =>
        {
            descriptor = d;
            d.MapAction(context => descriptor!.MapAction(static context => { }));
        }).Build();

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => mapper.Map<ExecutionTarget, ExecutionSource>(new ExecutionSource()));
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Derived profile interface action overrides remain authoritative")]
    public void Map_WithReimplementedProfileInterface_UsesReplacementActions()
    {
        // Arrange
        var profile = new ExecutionDerivedProfile { ReplacementActions = new[]
        {
            new ExecutionCallback(static context => ((ExecutionTarget)context.Target).Value = 99)
        } };
        var mapper = new MapperBuilder().AddProfile(profile).Build();

        // Act
        var result = mapper.Map<ExecutionTarget, ExecutionSource>(new ExecutionSource { Value = 1 });

        // Assert
        result.Value.ShouldBe(99);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Replacing a public action list entry remains observable")]
    public void Map_WhenActionIsReplacedBetweenCalls_UsesCurrentAction()
    {
        // Arrange
        var mapper = new MapperBuilder().AddProfile<ExecutionTarget, ExecutionSource>(d => d
            .MapMember(static s => s.Value, static (t, value) => t.Value = value)).Build();
        var source = new ExecutionSource { Value = 5 };
        var original = mapper.Map<ExecutionTarget, ExecutionSource>(source);

        // Act
        ((IList<IMapperAction>)mapper.Profiles[0].MapActions)[0] = new ExecutionCallback(static c => ((ExecutionTarget)c.Target).Value = 42);
        var updated = mapper.Map<ExecutionTarget, ExecutionSource>(source);

        // Assert
        original.Value.ShouldBe(5);
        updated.Value.ShouldBe(42);
    }
}
