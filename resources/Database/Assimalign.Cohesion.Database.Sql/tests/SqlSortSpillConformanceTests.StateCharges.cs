using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    public static IEnumerable<object[]> SpillStateFamilies()
    {
        string[] scalar = ["NULL", "TINYINT", "SMALLINT", "INT", "BIGINT", "REAL", "DOUBLE PRECISION", "NUMERIC(12,2)",
            "BOOLEAN", "DATE", "TIME", "TIMESTAMP", "TIMESTAMPTZ", "INTERVAL", "UUID"];
        foreach (string function in new[] { "MIN", "MAX", "COUNT" })
        {
            foreach (string type in scalar) { yield return [function, type]; }
        }
        foreach (string function in new[] { "SUM", "AVG" })
        {
            foreach (string type in scalar.Take(8)) { yield return [function, type]; }
        }
        foreach (string function in new[] { "BOOL_AND", "BOOL_OR", "EVERY" }) { yield return [function, "BOOLEAN"]; }
        yield return ["COUNT_ROWS", "NULL"];
        foreach (string type in new[] { "TEXT", "BINARY", "CHAR(160)" }) { yield return ["COUNT", type]; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash state charges bound measured marginal live bytes for every native kind and family (#1789)")]
    [MemberData(nameof(SpillStateFamilies))]
    public async Task PlainGroupStateBytes_NativeFamilies_ShouldBoundRetainedStates(string function, string type)
    {
        // Arrange: use the actual executor wrappers and accumulator, without plan/table costs.
        await using var engine = await Create("state-charge", 67108864);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE state_input(id INT)");
        string expression = type == "NULL" ? "NULL" : "CAST(NULL AS " + type + ")";
        string call = function == "COUNT_ROWS" ? "COUNT(*)" : $"{function}({expression})";
        var plan = (SqlGroupPlan)new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            $"SELECT id,{call} FROM state_input GROUP BY id").Statement.SqlExpression);
        var aggregate = plan.Aggregates.Single();
        MeasureStates(aggregate, type); // warm the exact factory, transition and accessors

        // Act: retain 4,096 then 16,384 states in the same preallocated root vector.
        long bytes = MeasureStates(aggregate, type);
        long charged = GroupStateBytes(null!, [aggregate]) - GroupStateBytes(null!, []);

        // Assert: no rounding tolerance; even one byte per state scales past a large grant.
        _output.WriteLine($"STATE_CHARGE|{function}|{type}|bytes_per_state={bytes / 12288d}|charged={charged}");
        (bytes + 12288 * 8L).ShouldBeLessThanOrEqualTo(12288 * charged);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureStates(SqlGroupAggregate aggregate, string type)
    {
        var roots = new object?[16384];
        Fill(0, 4096);
        long small = LiveHeap.Read();
        Fill(4096, 16384);
        long bytes = LiveHeap.Read() - small;
        GC.KeepAlive(roots);
        return bytes;

        void Fill(int first, int last)
        {
            for (int i = first; i < last; i++)
            {
                var accumulator = aggregate.Function.CreateAccumulator(default);
                object state = aggregate.Function is SqlExtremumFunction
                    ? NewExtremumState(accumulator, aggregate, null) : NewGroupState(accumulator, aggregate, null);
                var value = SqlValue.FromObject(StateValue(type, i));
                accumulator.AddResolved(new SqlArguments(aggregate.Arguments.Length == 0 ? [] : new ReadOnlySpan<SqlValue>(in value), default(SqlFunctionContext)));
                if (aggregate.Function is SqlExtremumFunction) { MirroredExtreme(state) = value; }
                roots[i] = state;
            }
        }
    }

    private static object? StateValue(string type, int i) => type switch
    {
        "NULL" => null, "TINYINT" => (sbyte)(i % 100), "SMALLINT" => (short)i, "INT" => i, "BIGINT" => (long)i,
        "REAL" => (float)i, "DOUBLE PRECISION" => (double)i, "NUMERIC(12,2)" => (decimal)i,
        "BOOLEAN" => i % 2 == 0, "DATE" => new DateOnly(2026, 1, 1).AddDays(i), "TIME" => new TimeOnly(i),
        "TIMESTAMP" => new DateTime(2026, 1, 1).AddTicks(i), "TIMESTAMPTZ" => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(i),
        "INTERVAL" => TimeSpan.FromTicks(i), "UUID" => new Guid(i, 0, 0, new byte[8]),
        "TEXT" => new string('x', 32), "BINARY" => new byte[32], "CHAR(160)" => new SqlCharacter(new string('x', 160)),
        "BYTE" => (byte)i, "USHORT" => (ushort)i, "UINT" => (uint)i, "ULONG" => (ulong)i, "CLRCHAR" => (char)i,
        _ => throw new InvalidOperationException()
    };

    private const string groupStateType = "Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor+GroupAggregateState, Assimalign.Cohesion.Database.Sql";
    private const string extremumStateType = "Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor+ExtremumGroupAggregateState, Assimalign.Cohesion.Database.Sql";

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    [return: UnsafeAccessorType(groupStateType)]
    private static extern object NewGroupState(SqlAggregateAccumulator accumulator, SqlGroupAggregate aggregate, SqlSubqueryValues? subqueries);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    [return: UnsafeAccessorType(extremumStateType)]
    private static extern object NewExtremumState(SqlAggregateAccumulator accumulator, SqlGroupAggregate aggregate, SqlSubqueryValues? subqueries);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Value")]
    private static extern ref SqlValue MirroredExtreme([UnsafeAccessorType(extremumStateType)] object state);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "PlainGroupStateBytes")]
    private static extern long GroupStateBytes(SqlPlanExecutor executor, IReadOnlyList<SqlGroupAggregate> aggregates);
}
