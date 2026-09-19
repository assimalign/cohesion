using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Mapping;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

/// <summary>Measures executable scalar comparisons and precise exclusions, plus materialization for every family.</summary>
public sealed class SqlMapperScalarTests
{
    /// <summary>Executable comparisons run on the engine; binary and floating comparisons reject before SQL emission.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Scalars: each family executes supported operators or rejects precisely")]
    [InlineData("Boolean")]
    [InlineData("Byte")]
    [InlineData("Int8")]
    [InlineData("Int16")]
    [InlineData("Int32")]
    [InlineData("Int64")]
    [InlineData("Float32")]
    [InlineData("Float64")]
    [InlineData("Decimal")]
    [InlineData("Text")]
    [InlineData("Binary")]
    [InlineData("Date")]
    [InlineData("Time")]
    [InlineData("Timestamp")]
    [InlineData("Offset")]
    [InlineData("Duration")]
    [InlineData("Guid")]
    public async Task QueryAsync_ScalarFamily_ShouldCompareOrderAndMaterialize(string family)
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var mapper = new MapperScalarMapper();
        var work = MappingUnitOfWork.Create(harness.Store);
        var values = SqlMapping.Register(work, mapper);
        MapperScalar lower = Create(1);
        MapperScalar upper = Create(2);
        values.Add(upper);
        values.Add(lower);
        await work.SaveChangesAsync(timeout.Token);

        // Act / Assert: each path invokes the parser, planner, evaluator, wire codec and generated reader.
        switch (family)
        {
            case "Boolean": await CheckAsync(harness, MapperScalarMapper.Columns.Boolean, lower.Boolean, upper.Boolean, timeout.Token); break;
            case "Byte": await CheckAsync(harness, MapperScalarMapper.Columns.Byte, lower.Byte, upper.Byte, timeout.Token); break;
            case "Int8": await CheckAsync(harness, MapperScalarMapper.Columns.Int8, lower.Int8, upper.Int8, timeout.Token); break;
            case "Int16": await CheckAsync(harness, MapperScalarMapper.Columns.Int16, lower.Int16, upper.Int16, timeout.Token); break;
            case "Int32": await CheckAsync(harness, MapperScalarMapper.Columns.Int32, lower.Int32, upper.Int32, timeout.Token); break;
            case "Int64": await CheckAsync(harness, MapperScalarMapper.Columns.Int64, lower.Int64, upper.Int64, timeout.Token); break;
            case "Float32":
                CheckUnsupported(MapperScalarMapper.Columns.Float32, float.MaxValue);
                await CheckOrderingAsync(harness, MapperScalarMapper.Columns.Float32, timeout.Token);
                break;
            case "Float64":
                CheckUnsupported(MapperScalarMapper.Columns.Float64, double.MaxValue);
                await CheckOrderingAsync(harness, MapperScalarMapper.Columns.Float64, timeout.Token);
                break;
            case "Decimal": await CheckAsync(harness, MapperScalarMapper.Columns.Decimal, lower.Decimal, upper.Decimal, timeout.Token); break;
            case "Text": await CheckAsync(harness, MapperScalarMapper.Columns.Text, lower.Text, upper.Text, timeout.Token); break;
            case "Binary":
                CheckUnsupported(MapperScalarMapper.Columns.Binary, upper.Binary);
                await CheckOrderingAsync(harness, MapperScalarMapper.Columns.Binary, timeout.Token);
                break;
            case "Date": await CheckAsync(harness, MapperScalarMapper.Columns.Date, lower.Date, upper.Date, timeout.Token); break;
            case "Time": await CheckAsync(harness, MapperScalarMapper.Columns.Time, lower.Time, upper.Time, timeout.Token); break;
            case "Timestamp": await CheckAsync(harness, MapperScalarMapper.Columns.Timestamp, lower.Timestamp, upper.Timestamp, timeout.Token); break;
            case "Offset": await CheckAsync(harness, MapperScalarMapper.Columns.Offset, lower.Offset, upper.Offset, timeout.Token); break;
            case "Duration": await CheckAsync(harness, MapperScalarMapper.Columns.Duration, lower.Duration, upper.Duration, timeout.Token); break;
            case "Guid": await CheckAsync(harness, MapperScalarMapper.Columns.Guid, lower.Guid, upper.Guid, timeout.Token); break;
            default: throw new ArgumentOutOfRangeException(nameof(family));
        }
        var rows = await harness.Store.QueryAsync(SqlMapping.Query(mapper).OrderBy(MapperScalarMapper.Columns.Id), timeout.Token);
        mapper.AreEqual(mapper.Capture(lower), mapper.Capture(rows[0])).ShouldBeTrue();
        mapper.AreEqual(mapper.Capture(upper), mapper.Capture(rows[1])).ShouldBeTrue();
    }

    private static void CheckUnsupported<T>(SqlColumn<MapperScalar, T> column, T value)
    {
        var query = SqlMapping.Query(new MapperScalarMapper());
        SqlPredicate<MapperScalar>[] predicates =
        [
            column.Equal(value), column.NotEqual(value), column.GreaterThan(value),
            column.GreaterThanOrEqual(value), column.LessThan(value), column.LessThanOrEqual(value),
        ];
        foreach (var predicate in predicates)
        {
            Should.Throw<NotSupportedException>(() => query.Where(predicate).ToCommand());
        }
    }

    private static async Task CheckAsync<T>(SqlMapperTestHarness harness, SqlColumn<MapperScalar, T> column,
        T lower, T upper, CancellationToken cancellationToken)
    {
        var query = SqlMapping.Query(new MapperScalarMapper());
        SqlPredicate<MapperScalar>[] predicates =
        [
            column.Equal(upper), column.NotEqual(lower), column.GreaterThan(lower),
            column.GreaterThanOrEqual(upper), column.LessThan(upper), column.LessThanOrEqual(lower),
        ];
        for (int index = 0; index < predicates.Length; index++)
        {
            var rows = await harness.Store.QueryAsync(query.Where(predicates[index]), cancellationToken);
            rows.ShouldHaveSingleItem().Id.ShouldBe(index < 4 ? 2 : 1);
        }
        await CheckOrderingAsync(harness, column, cancellationToken);
    }

    private static async Task CheckOrderingAsync<T>(SqlMapperTestHarness harness, SqlColumn<MapperScalar, T> column,
        CancellationToken cancellationToken)
    {
        var query = SqlMapping.Query(new MapperScalarMapper());
        (await harness.Store.QueryAsync(query.Distinct().OrderBy(column), cancellationToken)).Select(row => row.Id)
            .ShouldBe(new[] { 1, 2 });
        (await harness.Store.QueryAsync(query.OrderBy(column, descending: true), cancellationToken)).Select(row => row.Id)
            .ShouldBe(new[] { 2, 1 });
    }

    private static MapperScalar Create(int value)
        => new()
        {
            Id = value,
            Boolean = value == 2,
            Byte = (byte)value,
            Int8 = (sbyte)value,
            Int16 = (short)value,
            Int32 = value,
            Int64 = value,
            Float32 = value == 1 ? 1.25f : float.MaxValue,
            Float64 = value == 1 ? 1.25 : double.MaxValue,
            Decimal = value + 0.25m,
            Text = value == 1 ? "a" : "b",
            Binary = [(byte)value],
            Date = new DateOnly(2026, 1, value),
            Time = new TimeOnly(value, 0),
            Timestamp = new DateTime(2026, 1, value, 0, 0, 0, DateTimeKind.Utc),
            Offset = new DateTimeOffset(2026, 1, value, 0, 0, 0, TimeSpan.Zero),
            Duration = TimeSpan.FromHours(value),
            Guid = new Guid(value, 0, 0, new byte[8]),
        };
}
