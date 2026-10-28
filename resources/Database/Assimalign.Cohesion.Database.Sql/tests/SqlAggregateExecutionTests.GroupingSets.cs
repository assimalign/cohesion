using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlAggregateExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping sets: masks distinguish stored NULLs for one through four arguments")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GroupingSets_CubeMasks_ShouldUsePostgreSqlBitOrder(int width)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("masks");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string[] names = ["a", "b", "c", "d"];
        string arguments = string.Join(",", names.Take(width));
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT,c INT,d INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(NULL,NULL,NULL,NULL);");

        // Act
        await using var result = (DatabaseResultSet)await ExecuteAsync(session,
            $"SELECT {arguments}, GROUPING({arguments}) AS mask, GROUPING_ID({arguments}) AS alias_mask, COUNT(*) FROM t GROUP BY CUBE({arguments}) ORDER BY mask;");
        var rows = await ReadRowsAsync(result);

        // Assert
        result.Columns[width].Type.ShouldBe(DatabaseType.Int32);
        result.Columns[width].IsNullable.ShouldBeFalse();
        rows.Count.ShouldBe(1 << width);
        for (int mask = 0; mask < rows.Count; mask++)
        {
            rows[mask].Take(width).ShouldAllBe(value => value == null);
            rows[mask][width].ShouldBe(mask);
            rows[mask][width + 1].ShouldBe(mask);
            rows[mask][width + 2].ShouldBe(1L);
        }
        // Reversed and repeated arguments independently determine bit positions.
        CheckRows(await RowsAsync(session,
            "SELECT GROUPING(a,b,c,d), GROUPING(d,c,b,a), GROUPING(a,a) FROM t GROUP BY GROUPING SETS ((a,c), (b,d)) ORDER BY GROUPING(a,b,c,d);"),
            [[5, 10, 0], [10, 5, 3]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping sets: generated inputs equal independent UNION ALL aggregates")]
    [InlineData("CUBE(a,b,c)", false)]
    [InlineData("ROLLUP(a,b,c)", false)]
    [InlineData("a, CUBE(b,c)", false)]
    [InlineData("GROUPING SETS ((a,b,c),(a,c),(),(a,c))", false)]
    [InlineData("DISTINCT GROUPING SETS ((a,b,c),(a,c),(),(a,c))", true)]
    public async Task GroupingSets_GeneratedData_ShouldMatchUnionAll(string grouping, bool distinct)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("equivalence");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT,c INT,v INT,label TEXT,flag BOOLEAN);");
        await ExecuteAsync(session, "INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(0, 48).Select(i =>
            $"({(i % 7 == 0 ? "NULL" : (i % 3).ToString(CultureInfo.InvariantCulture))},{i % 2},{i % 4},{i % 5},'s{i % 5}',{(i % 3 == 0 ? "FALSE" : "TRUE")})")) + ";");
        int[] masks = grouping.StartsWith("CUBE", StringComparison.Ordinal) ? Enumerable.Range(0, 8).ToArray() :
            grouping.StartsWith("ROLLUP", StringComparison.Ordinal) ? [0, 1, 3, 7] :
            grouping.StartsWith("a,", StringComparison.Ordinal) ? [0, 1, 2, 3] : distinct ? [0, 2, 7] : [0, 2, 7, 2];
        const string aggregates = "COUNT(*),SUM(DISTINCT v) FILTER (WHERE flag),COUNT(DISTINCT label)," +
            "STRING_AGG(DISTINCT label,',' ORDER BY label) FILTER (WHERE flag),BOOL_AND(flag),BOOL_OR(flag),EVERY(flag),VARIANCE(v),STDDEV(v)";

        // Act
        var actual = await RowsAsync(session, $"SELECT a,b,c,GROUPING(a,b,c),{aggregates} FROM t GROUP BY {grouping};");
        string[] columns = ["a", "b", "c"];
        string union = string.Join(" UNION ALL ", masks.Select(mask =>
        {
            var included = columns.Where((_, i) => (mask & (1 << (2 - i))) == 0).ToArray();
            return "SELECT " + string.Join(",", columns.Select((column, i) => (mask & (1 << (2 - i))) == 0 ? column : "CAST(NULL AS INT)")) +
                $",{mask},{aggregates} FROM t" + (included.Length == 0 ? "" : " GROUP BY " + string.Join(",", included));
        }));
        var expected = await RowsAsync(session, union + ";");

        // Assert: compare bags; no implicit order is part of the grouping contract.
        actual.Select(Signature).Order().ShouldBe(expected.Select(Signature).Order());

        static string Signature(object?[] row) => string.Join("|", row.Select(value =>
            value is null ? "NULL" : Convert.ToString(value, CultureInfo.InvariantCulture)));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping sets: nested elements and duplicate sets retain independent states")]
    [InlineData("ROLLUP(GROUPING SETS ((a),(b)),c)", 5)]
    [InlineData("CUBE(GROUPING SETS ((a),(b)),c)", 6)]
    [InlineData("CUBE((a,b),c)", 4)]
    [InlineData("ROLLUP(a),ROLLUP(a)", 4)]
    [InlineData("DISTINCT ROLLUP(a),ROLLUP(a)", 2)]
    public async Task GroupingSets_NestedExpansion_ShouldKeepProductSemantics(string grouping, int count)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("nested");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT,c INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,2,3),(1,2,3);");

        // Act
        var rows = await RowsAsync(session, $"SELECT COUNT(*) FROM t GROUP BY {grouping};");

        // Assert
        rows.Count.ShouldBe(count);
        rows.ShouldAllBe(row => Equals(row[0], 2L));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Grouping sets: expression keys are NULL in every completed-row scope when absent")]
    public async Task GroupingSets_AbsentExpression_ShouldNullProjectionHavingAndOrdering()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("expressions");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,2),(1,3),(NULL,2);");

        // Act
        var rows = await RowsAsync(session, "SELECT (a+1)*2 AS k, b, GROUPING((a+1)*2,b)+10 AS mask,COUNT(*) " +
            "FROM t GROUP BY GROUPING SETS (((a+1)*2,b),((a+1)*2),()) " +
            "HAVING b IS NULL AND (GROUPING((a+1)*2,b)=1 OR GROUPING((a+1)*2,b)=3) ORDER BY GROUPING((a+1)*2,b), (a+1)*2 NULLS LAST;");

        // Assert
        CheckRows(rows, [[4m, null, 11m, 2L], [null, null, 11m, 1L], [null, null, 13m, 3L]]);
        CheckRows(await RowsAsync(session, "SELECT a,COUNT(*) AS n FROM t GROUP BY ROLLUP(a) ORDER BY GROUPING(a)+n DESC,a NULLS LAST;"),
            [[null,3L],[1,2L],[null,1L]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Grouping sets: masks never evaluate arguments and correlated reads use completed keys")]
    public async Task GroupingSets_KeyEvaluation_ShouldRunOncePerInputAndMaskCorrelations()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("group-evaluation");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observed_key", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("evaluation");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,2),(2,3);");

        // Act
        var masks = await RowsAsync(session, "SELECT GROUPING(observed_key(a)),COUNT(*) FROM t GROUP BY CUBE(observed_key(a));");
        var rows = await RowsAsync(session, "SELECT a,b,(SELECT a),(SELECT b),GROUPING(a,b) FROM t " +
            "GROUP BY GROUPING SETS ((a,a,b),(b),()) ORDER BY GROUPING(a,b),a,b;");

        // Assert
        calls.ShouldBe(2);
        masks.Count.ShouldBe(3);
        CheckRows(rows, [[1,2,1,2,0],[2,3,2,3,0],[null,2,null,2,2],[null,3,null,3,2],[null,null,null,null,3]]);
        const string queryKey = "(SELECT MAX(x.a) FROM t x,t y)";
        CheckRows(await RowsAsync(session, $"SELECT {queryKey},GROUPING({queryKey}),COUNT(*) FROM t GROUP BY GROUPING SETS (({queryKey}),()) ORDER BY GROUPING({queryKey});"),
            [[2,0,2L],[null,1,2L]]);
        CheckRows(await RowsAsync(session, "SELECT GROUPING(1/(a-a)) FROM t WHERE FALSE GROUP BY ROLLUP(1/(a-a));"), [[1]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Grouping sets: FILTER and volatile arguments evaluate once before feeding independent sets")]
    public async Task GroupingSets_VolatileInputs_ShouldEvaluateOncePerLogicalAggregateAndInput()
    {
        // Arrange
        int filters = 0, arguments = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("group-input-evaluation");
        builder.Functions.Add(SqlScalarFunction.Create<int, bool>("admit", value => { filters++; return value > 0; }));
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("argument", value => { arguments++; return 6 / value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("inputs");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(0),(1),(2);");

        // Act
        var rows = await RowsAsync(session, "SELECT a,GROUPING(a),COUNT(DISTINCT argument(a)) FILTER(WHERE admit(a))," +
            "SUM(argument(a)) FILTER(WHERE admit(a)) FROM t GROUP BY CUBE(a) ORDER BY GROUPING(a),a;");

        // Assert
        filters.ShouldBe(6);
        arguments.ShouldBe(4);
        CheckRows(rows, [[0,0,0L,null],[1,0,1L,6m],[2,0,1L,3m],[null,1,2L,9m]]);
        arguments = 0;
        await RowsAsync(session, "SELECT SUM(argument(a)),SUM(argument(a)) FROM t WHERE a>0 GROUP BY CUBE(a);");
        arguments.ShouldBe(4); // volatile logical calls are never merged
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping sets: only empty sets produce empty-input groups")]
    [InlineData("ROLLUP(a,b)", 1)]
    [InlineData("CUBE(a,b)", 1)]
    [InlineData("GROUPING SETS ((a),(b))", 0)]
    [InlineData("GROUPING SETS ((),(),(a))", 2)]
    [InlineData("DISTINCT GROUPING SETS ((),(),(a))", 1)]
    public async Task GroupingSets_EmptyInput_ShouldInitializeOnlyEmptySets(string grouping, int count)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("empty");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT);");

        // Act
        var rows = await RowsAsync(session, $"SELECT COUNT(*),SUM(a),BOOL_AND(a>0),STDDEV(a) FROM t GROUP BY {grouping};");

        // Assert
        rows.Count.ShouldBe(count);
        foreach (var row in rows) { row.ShouldBe(new object?[] { 0L, null, null, null }); }
        (await RowsAsync(session, "SELECT COUNT(*) FROM t GROUP BY ();"))[0][0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping sets: invalid references, placements and arguments have stable codes")]
    [InlineData("SELECT GROUPING(b) FROM t GROUP BY ROLLUP(a);", "CS01062")]
    [InlineData("SELECT GROUPING(a+1) FROM t GROUP BY a;", "CS01062")]
    [InlineData("SELECT GROUPING(SUM(a)) FROM t GROUP BY a;", "CS01062")]
    [InlineData("SELECT GROUPING(a) FROM t;", "CS05074")]
    [InlineData("SELECT COUNT(*) FROM t WHERE GROUPING(a)=0 GROUP BY a;", "CS05074")]
    [InlineData("SELECT SUM(GROUPING(a)) FROM t GROUP BY a;", "CS05074")]
    [InlineData("SELECT COUNT(*) FILTER(WHERE GROUPING(a)=0) FROM t GROUP BY a;", "CS05074")]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY GROUPING(a);", "CS05074")]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY ROLLUP(SUM(a));", "CS05017")]
    [InlineData("SELECT a FROM t GROUP BY a HAVING GROUPING(a);", "CS02003")]
    [InlineData("SELECT GROUPING() FROM t GROUP BY a;", "CS01063")]
    [InlineData("SELECT GROUPING(*) FROM t GROUP BY a;", "CS01063")]
    [InlineData("SELECT GROUPING(a) FILTER(WHERE TRUE) FROM t GROUP BY a;", "CS05072")]
    [InlineData("SELECT GROUPING(DISTINCT a) FROM t GROUP BY a;", "CS05032")]
    [InlineData("SELECT GROUPING(a ORDER BY a) FROM t GROUP BY a;", "CS05032")]
    [InlineData("SELECT (SELECT GROUPING(t.a) FROM t q GROUP BY q.b) FROM t GROUP BY a;", "CS01062")]
    [InlineData("CREATE TABLE invalid(v INT CHECK(GROUPING(v)=0));", "CS05074")]
    public async Task GroupingSets_InvalidContext_ShouldRefuseWithCode(string sql, string code)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("refusals");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,b INT);");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session, sql));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Grouping sets: expansion is bounded before DISTINCT and masks have at most 31 bits")]
    public async Task GroupingSets_Limits_ShouldRefuseBeforeExecution()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("limits");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        var expansion = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session,
            "SELECT COUNT(*) GROUP BY DISTINCT CUBE(" + string.Join(',', Enumerable.Repeat("1+0", 13)) + ");"));
        expansion.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05075");
        var arity = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session,
            "SELECT GROUPING(" + string.Join(',', Enumerable.Repeat("1+0", 32)) + ") GROUP BY 1+0;"));
        arity.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01063");
        string maximumArguments = string.Join(',', Enumerable.Repeat("1+0", 31));
        CheckRows(await RowsAsync(session, $"SELECT GROUPING({maximumArguments}) AS mask GROUP BY GROUPING SETS ((1+0),()) ORDER BY mask;"),
            [[0],[int.MaxValue]]);
        (await RowsAsync(session, "SELECT COUNT(*) GROUP BY DISTINCT CUBE(" + string.Join(',', Enumerable.Repeat("1+0", 12)) + ");")).Count.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping sets: state and buffered inputs consume the shared statement grant")]
    [InlineData("COUNT(*)", "CUBE(a)")]
    [InlineData("GROUPING(a),COUNT(*)", "a")]
    [InlineData("COUNT(DISTINCT v)", "GROUPING SETS ((),())")]
    [InlineData("STRING_AGG(label,',' ORDER BY v)", "GROUPING SETS ((),())")]
    [InlineData("STRING_AGG(label,',')", "GROUPING SETS ((),())")]
    public async Task GroupingSets_StateMemory_ShouldRefuseAndReleaseGrant(string aggregate, string grouping)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("group-memory", new() { StatementMemoryBudget = 4096 });
        var database = await engine.CreateDatabaseAsync("memory");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(a INT,v INT,label TEXT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 100).Select(i => $"({i},{i},'value-{i}')")) + ";");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session,
            $"SELECT {aggregate} FROM t GROUP BY {grouping};"));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 4096);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t GROUP BY ();"))[0][0].ShouldBe(100L);
        CheckRows(await RowsAsync(session, "SELECT COUNT(*) FROM t GROUP BY GROUPING SETS ((),());"), [[100L],[100L]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Grouping sets: empty groups charge per-state ordering metadata before allocating it")]
    public async Task GroupingSets_EmptyOrderedState_ShouldRespectStatementBudget()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("group-empty-memory", new() { StatementMemoryBudget = 4096 });
        var database = await engine.CreateDatabaseAsync("empty_state");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session,
            "SELECT SUM(1 ORDER BY " + string.Join(',', Enumerable.Repeat("1+0", 64)) + ") WHERE FALSE GROUP BY ();"));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Grouping sets: views reload and EXPLAIN describes expansion without changing plain grouping")]
    public async Task GroupingSets_PersistedView_ShouldReloadSubtotalMasks()
    {
        // Arrange
        string root = Path.GetFullPath(Path.Combine("_out", "sq-h2", "view-" + Guid.NewGuid().ToString("N")));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("group-views", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("views");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecuteAsync(session, "CREATE TABLE t(a INT);");
                await ExecuteAsync(session, "INSERT INTO t VALUES(1),(NULL);");
                await ExecuteAsync(session, "CREATE VIEW v AS SELECT a, GROUPING(a) AS mask, COUNT(*) AS n FROM t GROUP BY DISTINCT GROUPING SETS ((a),(),());");
            }

            // Act
            await using var reopened = SqlDatabaseEngine.Create("group-views", new() { RootPath = root });
            var next = await reopened.OpenDatabaseAsync("views", CancellationToken.None);
            await using var reader = await next.CreateSessionAsync(CancellationToken.None);
            var rows = await RowsAsync(reader, "SELECT a,mask,n FROM v ORDER BY mask,a NULLS LAST;");
            var explain = await RowsAsync(reader, "EXPLAIN SELECT a,COUNT(*) FROM t GROUP BY CUBE(a);");
            var plain = await RowsAsync(reader, "EXPLAIN SELECT a,COUNT(*) FROM t GROUP BY a;");

            // Assert
            CheckRows(rows, [[1, 0, 1L], [null, 0, 1L], [null, 1, 2L]]);
            explain.Any(row => row[5] is string detail && detail.Contains("grouping_sets=2", StringComparison.Ordinal)).ShouldBeTrue();
            plain.Any(row => row[5] is string detail && detail.Contains("grouping_sets", StringComparison.Ordinal)).ShouldBeFalse();
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }
}
