using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: new window and JSON overlap refusals keep the statement code (#1776)")]
    [InlineData(false, 7724L, 7692L)]
    [InlineData(true, 3728L, 3616L)]
    public async Task ExecuteAsync_ScalarGrantWithoutPlainBytes_ShouldKeepSpecificCode(bool json, long expectedPeak, long expectedRefusal)
    {
        // Arrange: each scalar scope fits alone; the outer collector remains live between calls.
        string sql = json ? "SELECT JSON_VALUE(doc,'$.a') FROM o"
            : "SELECT o.k,(SELECT SUM(u.k) OVER() FROM u WHERE u.k=o.k LIMIT 1) AS s FROM o ORDER BY o.k";
        string[] setup = json
            ? ["CREATE TABLE o(doc TEXT)", "INSERT INTO o VALUES" + string.Join(',', Enumerable.Repeat("('{\"a\":\"abcdefghij\",\"padding\":\"" + new string('x', 66) + "\"}')", 20))]
            : ["CREATE TABLE o(k INT)", "INSERT INTO o VALUES" + string.Join(',', Enumerable.Repeat("(1)", 20)), "CREATE TABLE u(k INT)", "INSERT INTO u VALUES(1)"];

        // Act
        var success = await Probe(setup, sql, 1_048_576);
        var boundary = await Probe(setup, sql, success.Peak - 1);
        var exact = await Probe(setup, sql, success.Peak);
        var original = await Probe(setup, sql, 1);
        string alone = json ? "SELECT JSON_VALUE(doc,'$.a') FROM o LIMIT 1" : "SELECT SUM(k) OVER() FROM u LIMIT 1";
        var aloneSuccess = await Probe(setup, alone, 1_048_576);
        var aloneRefused = await Probe(setup, alone, aloneSuccess.Peak - 1);
        var aloneExact = await Probe(setup, alone, aloneSuccess.Peak);

        // Assert: specific refusals that existed without plain buffers keep their identity.
        _output.WriteLine($"SCALAR_OVERLAP|json={json}|peak={success.Peak}|refused={boundary.Peak}|code={boundary.Code}|original={original.Code}:{original.Peak}");
        success.Code.ShouldBeNull();
        exact.Code.ShouldBeNull();
        boundary.Code.ShouldBe("CS05067");
        original.Code.ShouldBe(json ? "CS05094" : "CS05079");
        aloneRefused.Code.ShouldBe(original.Code);
        aloneExact.Code.ShouldBeNull();
        aloneSuccess.Peak.ShouldBe(json ? 1600 : 3924);
        aloneRefused.Peak.ShouldBe(json ? 1452 : 3892);
        _output.WriteLine($"SCALAR_ALONE|json={json}|{aloneSuccess.Peak}|{aloneRefused.Peak}|{aloneRefused.Code}");
        success.Peak.ShouldBe(expectedPeak);
        boundary.Peak.ShouldBe(expectedRefusal);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT memory: window input charges the wide nested loop inner before retaining it (#1776)")]
    public async Task ExecuteAsync_WindowWideInner_ShouldRefuseAtInnerBoundary()
    {
        // Arrange: no pair qualifies, so the projected window ledger never sees the wide cells.
        string[] setup = ["CREATE TABLE a(id INT)", "INSERT INTO a VALUES(0)", "CREATE TABLE b(v INT,note TEXT)",
            "INSERT INTO b VALUES" + string.Join(',', Enumerable.Range(1, 8).Select(i => $"({i},'{new string('x', 2000)}')"))];
        const string sql = "SELECT a.id,COUNT(*) OVER() FROM a JOIN b ON a.id>b.v";

        // Act
        var success = await Probe(setup, sql, 1_048_576);
        var refused = await Probe(setup, sql, success.Peak - 1);
        var exact = await Probe(setup, sql, success.Peak);

        // Assert
        _output.WriteLine($"WINDOW_INNER|{success.Peak}|{refused.Peak}|{refused.Code}|records={refused.Records}|pairs={refused.Pairs}");
        refused.Code.ShouldBe("CS05067");
        exact.Code.ShouldBeNull();
        success.Peak.ShouldBe(34656);
        refused.Peak.ShouldBe(30528);
        refused.Records.ShouldBe(9);
        refused.Pairs.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: window set operands retain independent plain reservations (#1776)")]
    [InlineData("INTERSECT", 43088L, 38928L)]
    [InlineData("EXCEPT ALL", 51472L, 51408L)]
    public async Task ExecuteAsync_WindowSetOperands_ShouldChargeConvertedRows(string operation, long expectedPeak, long expectedRefusal)
    {
        // Arrange: converted operands remain live while the next operand and count map grow.
        string[] setup = ["CREATE TABLE a(k INT,note TEXT)", "CREATE TABLE b(k INT,note TEXT)",
            $"INSERT INTO a VALUES(1,'{new string('x', 2000)}'),(2,'{new string('y', 2000)}')",
            $"INSERT INTO b VALUES(3,'{new string('x', 2000)}'),(4,'{new string('y', 2000)}')"];
        string sql = $"SELECT COUNT(*) OVER() FROM (SELECT k,note FROM a {operation} SELECT k,note FROM b) d";

        // Act
        var success = await Probe(setup, sql, 1_048_576);
        var refused = await Probe(setup, sql, success.Peak - 1);
        var exact = await Probe(setup, sql, success.Peak);

        // Assert
        _output.WriteLine($"WINDOW_SET|{operation}|{success.Peak}|{refused.Peak}|{refused.Code}");
        exact.Code.ShouldBeNull();
        refused.Code.ShouldBe("CS05067");
        success.Peak.ShouldBe(expectedPeak);
        refused.Peak.ShouldBe(expectedRefusal);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: budget refusal stops join, group and DISTINCT input work (#1776)")]
    [InlineData("SELECT a.k,b.k FROM a CROSS JOIN b", 4096L, 4032L, 12L, 25L)]
    [InlineData("SELECT a.k,b.k,COUNT(*) FROM a CROSS JOIN b GROUP BY a.k,b.k", 4096L, 4040L, 10L, 9L)]
    [InlineData("SELECT DISTINCT a.k,b.k FROM a CROSS JOIN b", 4096L, 3976L, 10L, 11L)]
    [InlineData("SELECT DISTINCT k FROM a", 1024L, 1024L, 5L, 0L)]
    [InlineData("SELECT DISTINCT 1000000/(k-100) FROM a", 1024L, 1024L, 5L, 0L)]
    [InlineData("SELECT a.k,b.k FROM a CROSS JOIN b WHERE 1000000/(100-a.k)>0", 4096L, 4032L, 12L, 25L)]
    public async Task ExecuteAsync_BudgetFailure_ShouldStopInput(string sql, long budget, long peak, long records, long pairs)
    {
        // Arrange: 800 candidates would be visited if a consumer refusal were drained.
        string[] setup = ["CREATE TABLE a(k INT)", "INSERT INTO a VALUES" + string.Join(',', Enumerable.Range(1, 100).Select(k => $"({k})")),
            "CREATE TABLE b(k INT)", "INSERT INTO b VALUES(1),(2),(3),(4),(5),(6),(7),(8)"];

        // Act
        var refused = await Probe(setup, sql, budget);

        // Assert
        _output.WriteLine($"REFUSAL_WORK|{sql}|{refused.Peak}|{refused.Records}|{refused.Pairs}|{refused.Code}");
        refused.Code.ShouldBe("CS05067");
        refused.Peak.ShouldBe(peak);
        refused.Records.ShouldBe(records);
        refused.Pairs.ShouldBe(pairs);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: correlated body reservations stay constant over ten thousand outer rows (#1776)")]
    [InlineData(false, 552L)]
    [InlineData(true, 648L)]
    public async Task ExecuteAsync_CorrelatedBodyAtScale_ShouldReuseInvocationGrant(bool forceBase, long expectedPeak)
    {
        // Arrange / Act
        var peaks = new List<long>();
        foreach (int count in new[] { 1000, 10000 })
        {
            string[] setup = ["CREATE TABLE o(k INT)", "INSERT INTO o VALUES" + string.Join(',', Enumerable.Repeat("(1)", count)),
                "CREATE TABLE u(k INT)", "INSERT INTO u VALUES(1),(2)"];
            var measured = await Probe(setup, "SELECT k,(SELECT COUNT(*) FROM u WHERE u.k=o.k) FROM o ORDER BY k LIMIT 1", 8192, forceBase);
            measured.Code.ShouldBeNull();
            peaks.Add(measured.Peak);
            _output.WriteLine($"CORRELATED_SCALE|base={forceBase}|{count}|{measured.Peak}|records={measured.Records}");
            measured.Records.ShouldBe(count * 3L);
        }

        // Assert
        peaks[1].ShouldBe(peaks[0]);
        peaks[0].ShouldBe(expectedPeak);
    }

    private static async Task<(long Peak, string? Code, long Records, long Pairs)> Probe(string[] setup, string sql, long budget, bool forceBase = false)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("select-fix-boundary");
        builder.Options.StatementMemoryBudget = budget;
        builder.Functions.Add(new MemoryRandomFunction());
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string statement in setup) { await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None); }
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        string? code = null;
        try { await Read(session, sql); }
        catch (DatabaseException failure) { code = failure.Diagnostics[0].Code; }
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
        return (metrics.PeakHashMemoryBytes, code, metrics.RecordsExamined, metrics.JoinCandidatePairs);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: caches first materialized in a window retain their statement charge (#1776)")]
    [InlineData(false, 20384L, 20184L)]
    [InlineData(true, 20192L, 19992L)]
    public async Task ExecuteAsync_WindowCreatedCache_ShouldChargeOuterSort(bool vector, long expectedPeak, long expectedRefusal)
    {
        // Arrange: the outer sort outgrows the window stage, making cache lifetime observable.
        string[] setup = ["CREATE TABLE big(k INT)", "INSERT INTO big VALUES(1),(2),(3)", "CREATE TABLE o(k INT)",
            "INSERT INTO o VALUES" + string.Join(',', Enumerable.Range(1, 100).Select(i => $"({i})"))];
        string sql = vector
            ? "SELECT (SELECT CASE WHEN COUNT(*) OVER()>0 THEN (SELECT COUNT(*) FROM big) ELSE 0 END FROM big LIMIT 1) AS c,o.k FROM o ORDER BY o.k"
            : "SELECT (SELECT COUNT(*) OVER() FROM (SELECT * FROM big) d LIMIT 1) AS c,o.k FROM o ORDER BY o.k";

        // Act
        var success = await Probe(setup, sql, 1_048_576);
        var refused = await Probe(setup, sql, success.Peak - 1);
        var exact = await Probe(setup, sql, success.Peak);

        // Assert
        _output.WriteLine($"WINDOW_CACHE_LIFETIME|vector={vector}|{success.Peak}|{refused.Peak}|{refused.Code}");
        exact.Code.ShouldBeNull();
        refused.Code.ShouldBe("CS05067");
        success.Peak.ShouldBe(expectedPeak);
        refused.Peak.ShouldBe(expectedRefusal);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT memory: nested window cache transfer preserves earlier parent vectors (#1776)")]
    public async Task ExecuteAsync_NestedWindowCache_ShouldTransferOnlyStageAdditions()
    {
        // Arrange: the first vector belongs to the outer ledger before the inner stage starts.
        string[] setup = ["CREATE TABLE big(k INT)", "INSERT INTO big VALUES(1),(2),(3)", "CREATE TABLE o(k INT)",
            "INSERT INTO o VALUES" + string.Join(',', Enumerable.Range(1, 100).Select(i => $"({i})"))];
        const string sql = "SELECT CASE WHEN COUNT(*) OVER()>0 THEN (SELECT MAX(k) FROM big)+" +
            "(SELECT CASE WHEN COUNT(*) OVER()>0 THEN (SELECT COUNT(*) FROM big) ELSE 0 END FROM big LIMIT 1) " +
            "ELSE 0 END,o.k FROM o ORDER BY o.k";

        // Act
        var success = await Probe(setup, sql, 1_048_576);
        var refused = await Probe(setup, sql, success.Peak - 1);
        var exact = await Probe(setup, sql, success.Peak);

        // Assert
        _output.WriteLine($"NESTED_WINDOW_CACHE|{success.Peak}|{refused.Peak}|{refused.Code}");
        exact.Code.ShouldBeNull();
        refused.Code.ShouldBe("CS05067");
        success.Peak.ShouldBe(41560);
        refused.Peak.ShouldBe(41536);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT memory: ordered DISTINCT representative growth reserves before replacement (#1776)")]
    public async Task ExecuteAsync_LongerOrderedRepresentative_ShouldReserveGrowth()
    {
        // Arrange: DISTINCT arguments are equal; their separately evaluated ordering spellings differ.
        // The same expression appears in both places, as the SQL DISTINCT ordering rule requires.
        const string sql = "SELECT STRING_AGG(DISTINCT spelling(note),',' ORDER BY spelling(note)) FROM t";
        var measured = await Run(1_048_576, false);

        // Act
        var refused = await Run(measured - 1, true);
        await Run(measured, false);

        // Assert
        _output.WriteLine($"REPRESENTATIVE|{measured}|{refused}");
        measured.ShouldBe(1944);
        refused.ShouldBe(1704);
        (await Run(1511, true)).ShouldBe(1384);

        async Task<long> Run(long budget, bool refuses)
        {
            int calls = 0;
            var builder = SqlDatabaseEngine.CreateBuilder("representative-growth");
            builder.Options.StatementMemoryBudget = budget;
            builder.Functions.Add(SqlScalarFunction.Create("spelling", (string value) => ++calls % 2 == 1 ? "a" : value));
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE t(note TEXT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO t VALUES('z'),('AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA')", cancellationToken: CancellationToken.None);
            if (refuses)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(() => Read(session, sql));
                failure.Diagnostics.ShouldContain(item => item.Code == "CS05067");
            }
            else
            {
                await using var rows = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
                await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe("a"); }
                calls.ShouldBe(4);
            }
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            return session.LastStatementMetrics.PeakHashMemoryBytes;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: correlated derived input stays charged while its sibling runs (#1776)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TwoCorrelatedDerivedInputs_ShouldChargeLiveSibling(bool forceBase)
    {
        // Arrange
        SqlStatementMetrics? current = null;
        var observed = new List<long>();
        var builder = SqlDatabaseEngine.CreateBuilder("derived-sibling-overlap");
        builder.Functions.Add(SqlScalarFunction.Create("observe_second", (long value) =>
        { observed.Add(current!.HashMemoryBytes); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE o(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO o VALUES(1)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,1),(1,2)", cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;

        // Act
        try
        {
            SqlStatementMetrics.AfterMemoryReservation = (metrics, _) => current = metrics;
            await Read(session, "SELECT o.k,(SELECT COUNT(*) FROM (SELECT k,v FROM t WHERE k=o.k) d1 " +
                "JOIN (SELECT k,observe_second(v) AS v FROM t WHERE k=o.k) d2 ON d1.v=d2.v) FROM o");
        }
        finally { SqlStatementMetrics.AfterMemoryReservation = null; }

        // Assert
        _output.WriteLine($"DERIVED_SIBLING|base={forceBase}|{string.Join(',', observed)}");
        observed.ShouldBe(forceBase ? new long[] { 272, 408 } : new long[] { 528, 664 });
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

#if !DEBUG
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: numeric DISTINCT allocations are bounded per query (#1776)")]
    [InlineData("BIGINT", false)]
    [InlineData("DECIMAL(20,1)", true)]
    public async Task ExecuteAsync_DistinctNumericAllocation_ShouldKeepQueryBudget(string type, bool fractional)
    {
        // Arrange: all keys are unique and outside the Int32 fast hash path.
        await using var engine = SqlDatabaseEngine.Create("distinct-numeric-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE n(k {type})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO n VALUES" + string.Join(',', Enumerable.Range(0, 1000).Select(i =>
            $"({2147483648L + i}{(fractional ? ".5" : "")})")), cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql("SELECT DISTINCT k FROM n");
        Measure(8);

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(8)); }

        // Assert
        _output.WriteLine($"DISTINCT_ALLOCATION|{type}|{minimum / 8d} B/query");
        minimum.ShouldBeLessThanOrEqualTo(8L * (fractional ? 900_000 : 600_000));

        long Measure(int iterations)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                pending.IsCompletedSuccessfully.ShouldBeTrue();
                var rows = pending.Result.ShouldBeOfType<SqlMaterializedResultSet>();
                rows.Rows.Count.ShouldBe(1000);
                rows.DisposeAsync().GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
#endif
}
