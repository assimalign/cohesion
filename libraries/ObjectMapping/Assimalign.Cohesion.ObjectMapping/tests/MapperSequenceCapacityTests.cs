using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ObjectMapping.Tests.TestObjects;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperSequenceCapacityTests
{
    [Theory(DisplayName = "Cohesion Test [ObjectMapping] - Sequence: Should map counted and streaming inputs eagerly once")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    public void Map_Sequence_ShouldPreserveEagerSinglePass(int count, bool counted)
    {
        // Arrange
        int mapped = 0;
        int visited = 0;
        IMapper mapper = new MapperBuilder().AddProfile<SequenceTarget, SequenceSource>(profile =>
            profile.MapMember(source => { mapped++; return source.Value; }, static (target, value) => target.Value = value)).Build();
        SequenceSource[] items = Enumerable.Range(0, count).Select(value => new SequenceSource { Value = value }).ToArray();
        IEnumerable<SequenceSource> sources = counted ? items : items.Select(item => { visited++; return item; });

        // Act
        IEnumerable<SequenceTarget> result = mapper.Map<SequenceTarget, SequenceSource>(sources);

        // Assert
        mapped.ShouldBe(count);
        visited.ShouldBe(counted ? 0 : count);
        result.ShouldBeOfType<List<SequenceTarget>>();
        result.Select(item => item.Value).ShouldBe(Enumerable.Range(0, count));
        result.Select(item => item.Value).ShouldBe(Enumerable.Range(0, count));
        mapped.ShouldBe(count);
        visited.ShouldBe(counted ? 0 : count);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectMapping] - Sequence: Should preserve the existing null input exception")]
    public void Map_NullSequence_ShouldPreserveException()
    {
        // Arrange
        IMapper mapper = new MapperBuilder().Build();
        IEnumerable<SequenceSource> sources = null!;

        // Act / Assert
        Should.Throw<NullReferenceException>(() => mapper.Map<SequenceTarget, SequenceSource>(sources));
    }
}
