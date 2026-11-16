using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectMemoryTests
{
    // Deterministic volatile fixture: RANDOM is an application function in this engine.
    private sealed class MemoryRandomFunction : SqlScalarFunction
    {
        internal MemoryRandomFunction() : base("random", [], SqlType.BigInt) { }
        protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => SqlValue.FromInt64(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: live plain caches can fill a window grant first (#1776)")]
    [InlineData("SELECT (SELECT COUNT(*) FROM t),COUNT(*) OVER() FROM u", 4200L, 3712L)]
    [InlineData("WITH c AS (SELECT k FROM t) SELECT k,COUNT(*) OVER() FROM c", 4603L, 4580L)]
    public async Task ExecuteAsync_WindowCacheOverlap_ShouldUsePlainRefusal(string sql, long budget, long expectedPeak)
    {
        // Arrange / Act: both caches used to be adopted by the window ledger.
        string[] setup = ["CREATE TABLE t(k INT)", "INSERT INTO t VALUES(1),(1),(2)",
            "CREATE TABLE u(k INT)", "INSERT INTO u VALUES(1),(2)"];
        var result = await Probe(setup, sql, budget);

        // Assert: the accepted failure-to-failure code change is deliberate.
        _output.WriteLine($"WINDOW_CACHE_CODE|{sql}|{result.Peak}|{result.Code}");
        result.Code.ShouldBe("CS05067");
        result.Peak.ShouldBe(expectedPeak);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: fail-fast covers group input errors and normalized joins (#1776)")]
    [InlineData("SELECT a.k,b.k,COUNT(*) FROM a CROSS JOIN b WHERE 1000000/(100-a.k)>0 GROUP BY a.k,b.k")]
    [InlineData("SELECT a.k,b.k FROM a RIGHT JOIN b ON a.k>=b.k")]
    [InlineData("SELECT a.k,b.k FROM a FULL JOIN b ON a.k=b.k")]
    public async Task ExecuteAsync_TreeAndGroupRefusal_ShouldStopCandidateWork(string sql)
    {
        // Arrange / Act
        string[] setup = ["CREATE TABLE a(k INT)", "INSERT INTO a VALUES" + string.Join(',', Enumerable.Range(1, 100).Select(k => $"({k})")),
            "CREATE TABLE b(k INT)", "INSERT INTO b VALUES(1),(2),(3),(4),(5),(6),(7),(8)"];
        var result = await Probe(setup, sql, 4096);

        // Assert
        _output.WriteLine($"TREE_GROUP_REFUSAL|{sql}|{result.Peak}|{result.Records}|{result.Pairs}|{result.Code}");
        result.Code.ShouldBe("CS05067");
        result.Peak.ShouldBe(sql.Contains("GROUP BY", StringComparison.Ordinal) ? 4040 : 4056);
        result.Records.ShouldBe(sql.Contains("GROUP BY", StringComparison.Ordinal) ? 10 : 40);
        result.Pairs.ShouldBe(sql.Contains("GROUP BY", StringComparison.Ordinal) ? 9 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: sequence allocations before refusal remain consumed (#1776)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SequencePredicateRefusal_ShouldPinNextPosition(bool forceBase)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("sequence-predicate-refusal");
        builder.Options.StatementMemoryBudget = 1024;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE SEQUENCE s", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(1, 100).Select(k => $"({k})")), cancellationToken: CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => Read(session, "SELECT DISTINCT k FROM t WHERE nextval('s')>0"));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        await using var next = (DatabaseResultSet)await session.ExecuteAsync("SELECT nextval('s')", cancellationToken: CancellationToken.None);
        long position = 0;
        await foreach (var row in next.GetRowsAsync(CancellationToken.None)) { position = Convert.ToInt64(row.GetValue(0)); }

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CS05067");
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PlainSelectMemoryBytes.ShouldBe(0);
        _output.WriteLine($"SEQUENCE_REFUSAL|base={forceBase}|{metrics.PeakHashMemoryBytes}|{metrics.RecordsExamined}|next={position}");
        metrics.PeakHashMemoryBytes.ShouldBe(forceBase ? 960 : 1024);
        metrics.RecordsExamined.ShouldBe(forceBase ? 11 : 5);
        position.ShouldBe(forceBase ? 12 : 6);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT memory: standalone grouped OFFSET retains its admission boundary (#1776)")]
    public async Task ExecuteAsync_GroupedOffset_ShouldRefuseBeforeOutput()
    {
        // Arrange / Act
        string[] setup = ["CREATE TABLE t(k INT)", "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(1, 1001).Select(k => $"({k})"))];
        var result = await Probe(setup, "SELECT k FROM t GROUP BY k ORDER BY k LIMIT 1 OFFSET 1000", 8192);

        // Assert
        result.Code.ShouldBe("CS05067");
        result.Peak.ShouldBe(8160);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: volatile window final sort also charges its plain sources (#1776)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_VolatileWindowSort_ShouldPinOverlappingLists(bool forceBase)
    {
        // Arrange / Act: sources and sorted each charge ledger-held augmented rows again.
        string[] setup = ["CREATE TABLE t(k INT)", "INSERT INTO t VALUES(1),(2),(3)"];
        var result = await Probe(setup, "SELECT random(),ROW_NUMBER() OVER(ORDER BY k) FROM t ORDER BY k", 1_048_576, forceBase);

        // Assert: this carried cost is intentionally unchanged.
        result.Code.ShouldBeNull();
        _output.WriteLine($"VOLATILE_WINDOW_SORT|base={forceBase}|{result.Peak}");
        result.Peak.ShouldBe(3860);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT memory: independent vectors keep their lower resting window charge (#1776)")]
    public async Task ExecuteAsync_WindowIndependentVector_ShouldPinBudgetPair()
    {
        // Arrange / Act
        string[] setup = ["CREATE TABLE t(k INT)", "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(1, 100).Select(k => $"({k})")), "CREATE TABLE big(k INT)",
            "INSERT INTO big VALUES" + string.Join(',', Enumerable.Range(1, 100).Select(k => $"({k})"))];
        const string sql = "SELECT k,COUNT(*) OVER(),k IN (SELECT k FROM big) FROM t";
        var measured = await Probe(setup, sql, 1_048_576);
        var exact = await Probe(setup, sql, measured.Peak);
        var shortGrant = await Probe(setup, sql, measured.Peak - 1);

        // Assert
        _output.WriteLine($"VECTOR_WINDOW_PAIR|{measured.Peak}|{shortGrant.Peak}|{shortGrant.Code}");
        exact.Code.ShouldBeNull();
        shortGrant.Code.ShouldBe("CS05067");
        measured.Peak.ShouldBe(37712);
        shortGrant.Peak.ShouldBe(27424);
    }
}
