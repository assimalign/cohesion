using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Mapping;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

/// <summary>Runs every exposed query operator through the real parser, planner and executor.</summary>
public sealed class SqlMapperQueryTests
{
    /// <summary>Each predicate is executed against stored rows, including SQL null and Boolean semantics.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Query: all predicate forms execute on the real engine")]
    [InlineData("equal", new[] { 2 })]
    [InlineData("not equal", new[] { 1, 3 })]
    [InlineData("greater", new[] { 3 })]
    [InlineData("greater or equal", new[] { 2, 3 })]
    [InlineData("less", new[] { 1 })]
    [InlineData("less or equal", new[] { 1, 2 })]
    [InlineData("null", new[] { 1 })]
    [InlineData("not null", new[] { 2, 3 })]
    [InlineData("equal null", new[] { 1 })]
    [InlineData("not equal null", new[] { 2, 3 })]
    [InlineData("and", new[] { 2 })]
    [InlineData("or", new[] { 1, 3 })]
    [InlineData("not", new[] { 1, 3 })]
    [InlineData("nested", new[] { 2, 3 })]
    [InlineData("parameter escaping", new int[0])]
    public async Task QueryAsync_PredicateForms_ShouldExecute(string operation, int[] expected)
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        var parents = SqlMapping.Register(work, new MapperParentMapper());
        parents.Add(new MapperParent { Id = 3, Name = "third" });
        parents.Add(new MapperParent { Id = 1, Name = null });
        parents.Add(new MapperParent { Id = 2, Name = "second" });
        await work.SaveChangesAsync(timeout.Token);
        var id = MapperParentMapper.Columns.Id;
        var name = MapperParentMapper.Columns.Name;
        var predicate = operation switch
        {
            "equal" => id.Equal(2),
            "not equal" => id.NotEqual(2),
            "greater" => id.GreaterThan(2),
            "greater or equal" => id.GreaterThanOrEqual(2),
            "less" => id.LessThan(2),
            "less or equal" => id.LessThanOrEqual(2),
            "null" => name.IsNull(),
            "not null" => name.IsNotNull(),
            "equal null" => name.Equal(null),
            "not equal null" => name.NotEqual(null),
            "and" => id.GreaterThan(1).And(id.LessThan(3)),
            "or" => id.Equal(1).Or(id.Equal(3)),
            "not" => id.Equal(2).Not(),
            "nested" => id.Equal(1).Or(id.Equal(2)).And(id.Equal(1).Not()).Or(id.Equal(3)),
            "parameter escaping" => name.Equal("x'); DROP TABLE mapper_parents; --"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        // Act: QueryAsync sends generated SQL to the actual parser/planner over the SQL protocol.
        var rows = await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper())
            .Where(predicate).OrderBy(id), timeout.Token);

        // Assert.
        rows.Select(row => row.Id).ShouldBe(expected);
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token)).Count.ShouldBe(3);
    }

    /// <summary>The complete ordering, pagination and distinct surface composes without rejected SQL.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Query: ordering pagination and distinct execute on the real engine")]
    [InlineData("ascending", new[] { 1, 2, 3 })]
    [InlineData("descending", new[] { 3, 2, 1 })]
    [InlineData("then ascending", new[] { 1, 2, 3 })]
    [InlineData("then descending", new[] { 2, 1, 3 })]
    [InlineData("take", new[] { 1, 2 })]
    [InlineData("skip", new[] { 2, 3 })]
    [InlineData("take zero", new int[0])]
    [InlineData("skip zero", new[] { 1, 2, 3 })]
    [InlineData("skip all", new int[0])]
    [InlineData("distinct", new[] { 1, 2, 3 })]
    [InlineData("composed", new[] { 2 })]
    public async Task QueryAsync_QueryForms_ShouldExecute(string operation, int[] expected)
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        var parents = SqlMapping.Register(work, new MapperParentMapper());
        parents.Add(new MapperParent { Id = 3, Name = "b" });
        parents.Add(new MapperParent { Id = 1, Name = "a" });
        parents.Add(new MapperParent { Id = 2, Name = "a" });
        await work.SaveChangesAsync(timeout.Token);
        var id = MapperParentMapper.Columns.Id;
        var name = MapperParentMapper.Columns.Name;
        var query = SqlMapping.Query(new MapperParentMapper());
        query = operation switch
        {
            "ascending" => query.OrderBy(id),
            "descending" => query.OrderBy(id, descending: true),
            "then ascending" => query.OrderBy(name).ThenBy(id),
            "then descending" => query.OrderBy(name).ThenBy(id, descending: true),
            "take" => query.OrderBy(id).Take(2),
            "skip" => query.OrderBy(id).Skip(1),
            "take zero" => query.OrderBy(id).Take(0),
            "skip zero" => query.OrderBy(id).Skip(0),
            "skip all" => query.OrderBy(id).Skip(3),
            "distinct" => query.Distinct().OrderBy(id),
            "composed" => query.Where(id.GreaterThan(0)).Where(name.IsNotNull()).Distinct()
                .OrderBy(name).ThenBy(id).Skip(1).Take(1),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        // Act / Assert.
        (await harness.Store.QueryAsync(query, timeout.Token)).Select(row => row.Id).ShouldBe(expected);
    }

    /// <summary>Unsupported argument values fail while constructing SQL instead of reaching the server.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Query: invalid pagination null ordering and foreign column fail precisely")]
    public void Query_InvalidArguments_ShouldRejectBeforeSqlExecution()
    {
        // Arrange.
        var query = SqlMapping.Query(new MapperParentMapper());
        var foreignColumn = new SqlColumn<MapperParent, int>("another_table", "Id");
        var unknownColumn = new SqlColumn<MapperParent, int>("mapper_parents", "Missing");
        var forgedType = new SqlColumn<MapperParent, Guid>("mapper_parents", "Id");

        // Act / Assert.
        Should.Throw<ArgumentOutOfRangeException>(() => query.Take(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => query.Skip(-1));
        Should.Throw<ArgumentException>(() => MapperParentMapper.Columns.Name.GreaterThan(null));
        Should.Throw<ArgumentException>(() => query.Where(foreignColumn.Equal(1)).ToCommand());
        Should.Throw<ArgumentException>(() => query.OrderBy(unknownColumn).ToCommand());
        Should.Throw<ArgumentException>(() => query.Where(forgedType.Equal(Guid.NewGuid())).ToCommand());
        Should.Throw<InvalidOperationException>(() => query.ThenBy(MapperParentMapper.Columns.Id));
        Should.Throw<ArgumentException>(() => MapperScalarMapper.Columns.Float32.Equal(float.PositiveInfinity));
        Should.Throw<ArgumentException>(() => MapperScalarMapper.Columns.Float64.Equal(double.NaN));
    }
}
