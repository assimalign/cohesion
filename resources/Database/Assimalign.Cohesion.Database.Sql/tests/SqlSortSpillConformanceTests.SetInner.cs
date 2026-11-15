using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - UNION filters duplicate-heavy input within the default four-grant quota (#1789)")]
    public async Task ExecuteAsync_UnionSeenFilter_ShouldKeepDefaultQuota()
    {
        // Arrange: both sources exceed ten grants in retained rows. The bounded
        // seen filter sends each duplicate batch's unseen keys to hash partitions.
        var builder = SqlDatabaseEngine.CreateBuilder("union-seen-quota");
        builder.Options.RootPath = Path.Combine(_root, "union-seen-quota");
        builder.Options.StatementMemoryBudget = 262144; builder.Options.CheckpointJournalSize = 65536;
        await using var engine = await builder.BuildAsync();
        engine.StatementTempSpaceQuota.ShouldBe(1048576);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session, 32768);
        // Act
        await using (var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id%1200 AS id FROM t UNION SELECT id%1200 FROM t"))
        {
            // Assert: the base's first encounters are the integers 0 through 1199.
            (await Rows(result)).ShouldBe(Enumerable.Range(0, 1200).Select(value => new RowValues([(long)value])).ToArray());
        }
        var metrics = session.LastStatementMetrics!;
        SqlSortSpillMetrics.For(metrics).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0);
        SqlSortSpillMetrics.For(metrics)!.PeakBytes.ShouldBeLessThanOrEqualTo(1048576);
        metrics.HashMemoryBytes.ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Fitting mixed joins preserve the base EXPLAIN hash policy without spill fields (#1789)")]
    public async Task ExecuteAsync_FittingMixedExplain_ShouldKeepBasePolicy()
    {
        // Arrange: the archived planner/dispatcher excludes a tree with a non-hash
        // sibling from hash permission. Actual spill status can still report a spill.
        await using var engine = await Create("mixed-fitting-explain", 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        await session.ExecuteAsync("CREATE TABLE p(id INT)"); await session.ExecuteAsync("INSERT INTO p VALUES(0),(1),(4001)");
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            "EXPLAIN ANALYZE SELECT u.id,t.id,p.id FROM (u CROSS JOIN t) FULL JOIN p ON t.id=p.id");
        var description = string.Join('\n', (await Rows(result)).SelectMany(row => row.Cells.OfType<string>()));
        // Assert
        description.ShouldContain("hash_budget=no_spill; hash_status=fitting");
        description.ShouldNotContain("spilled_bytes="); description.ShouldNotContain("spill_runs=");
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_distinct")]
    private static extern ref StorageTempRun? SetOperandRun(SqlSpilledResultSet result);

    [Theory(DisplayName = "Cohesion Test [Sql] - Operand result close failure keeps success or cancellation after the cursor drains (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_SetOperandClose_ShouldKeepOutcome(bool cancel)
    {
        // Arrange: the ordered operand spills while UNION's two keys fit. Inject
        // only after its actual result cursor has delivered every converted row.
        await using var engine = await Create("set-operand-close", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        using var cancellation = new CancellationTokenSource(); bool injected = false, closed = false;
        OperationCanceledException? cancellationFailure = null;
        SqlRowCollector.ObserveBuffer = (phase, value) =>
        {
            if (phase != "set-operand-drained" || injected) { return; }
            injected = true;
            var result = (SqlSpilledResultSet)value;
            result.IsStageResult.ShouldBeTrue();
            var runs = Sorted(result) is { } sorted ? Runs(sorted) : new[] { SetOperandRun(result)! };
            runs.Length.ShouldBeGreaterThan(0);
            foreach (var run in runs)
            { RunFile(run) = new TableAsFailedCloseStream(RunFile(run).SafeFileHandle, () => closed = true); }
            if (cancel) { cancellation.Cancel(); throw cancellationFailure = new OperationCanceledException(cancellation.Token); }
        };
        try
        {
            // Act
            const string sql = "(SELECT id%2 AS value FROM t ORDER BY id) UNION SELECT 0";
            if (cancel)
            {
                var failure = await Record.ExceptionAsync(async () => await session.ExecuteAsync(sql, cancellationToken: cancellation.Token));
                failure.ShouldNotBeNull(); failure.ShouldBeSameAs(cancellationFailure);
                failure.ShouldBeAssignableTo<OperationCanceledException>();
                WindowFailureData(failure, nameof(SqlSpilledResultSet)).ShouldBeTrue();
            }
            else
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
                result.Columns.Single().Type.ShouldBe(DatabaseType.Int64);
                (await Rows(result)).ShouldBe(new[] { new RowValues([0L]), new RowValues([1L]) });
            }
            // Assert
            injected.ShouldBeTrue(); closed.ShouldBeTrue(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveBuffer = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Set/inner quota and floor failures keep the original memory refusal (#1789)")]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public async Task ExecuteAsync_SetInnerTempFailure_ShouldPreserveRefusal(bool inner, int cause)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("set-inner-temp-refusal");
        builder.Options.TempDirectory = Path.Combine(_root, "temp-refusal"); builder.Options.StatementMemoryBudget = 262144;
        if (cause == 0) { builder.Options.TempSpaceQuota = 0; }
        if (cause == 1) { builder.Options.StatementTempSpaceQuota = 0; }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        if (cause == 2) { Available(engine.TempSpace!) = () => 0; ProbeTick(engine.TempSpace!) = 0; }
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(inner
            ? "SELECT u.id FROM u CROSS JOIN t" : "SELECT id FROM t UNION ALL SELECT id FROM t"));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05067");
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0); session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Set hash repartition limits refuse and release without changing fitting semantics (#1789)")]
    [InlineData("UNION")] [InlineData("INTERSECT ALL")] [InlineData("EXCEPT")]
    public async Task ExecuteAsync_SetRepartitionLimit_ShouldRefuseAndRelease(string operation)
    {
        // Arrange: force all keys into one bucket at every depth, independently of process seed.
        await using var engine = await Create("set-depth-limit", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session, 32768);
        SqlPlanExecutor.HashSpillPartitionHash = _ => 0;
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"SELECT id FROM t {operation} SELECT id FROM t"));
            // Assert
            failure.Diagnostics[0].Code.ShouldBe("CS05067");
            SqlHashSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull().Passes.ShouldBeGreaterThan(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlPlanExecutor.HashSpillPartitionHash = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Set operands, volatile ON and pushed filters keep the first error, calls, sequence and visits (#1789)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ExecuteAsync_SetInnerFirstError_ShouldKeepFittingOutcome(int shape)
    {
        // Arrange
        var logs = new[] { new List<long>(), new List<long>() };
        var outcomes = new List<(string? Code, string Message, long Records, long Pairs, object? Sequence)>();
        for (int execution = 0; execution < 2; execution++)
        {
            int index = execution;
            var builder = SqlDatabaseEngine.CreateBuilder("set-inner-error");
            builder.Options.TempDirectory = Path.Combine(_root, "error-" + execution);
            builder.Options.StatementMemoryBudget = execution == 0 ? 67108864 : 262144;
            builder.Options.StatementTempSpaceQuota = 134217728;
            builder.Functions.Add(SqlScalarFunction.Create("probe", (long outer, long value) =>
            { logs[index].Add(outer * 10000 + value); if (value == 3000) { throw new InvalidOperationException("injected first error"); } return value; }));
            await using var engine = await builder.BuildAsync();
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
            await session.ExecuteAsync("SELECT nextval('s')");
            string sql = shape switch
            {
                0 => "SELECT nextval('s'),probe(0,id) FROM t UNION ALL SELECT nextval('s'),probe(1,id) FROM t",
                1 => "SELECT nextval('s'),t.id FROM u JOIN t ON probe(u.id,t.id)>=0",
                _ => "SELECT nextval('s'),t.id FROM u JOIN t ON t.id>=u.id WHERE probe(0,t.id)>=0",
            };
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
            var metrics = session.LastStatementMetrics!;
            await using var sequence = (DatabaseResultSet)await session.ExecuteAsync("SELECT currval('s')");
            outcomes.Add((failure.Diagnostics[0].Code, failure.Message, metrics.RecordsExamined, metrics.JoinCandidatePairs, (await Rows(sequence))[0].Cells[0]));
            // Assert
            metrics.HashMemoryBytes.ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        outcomes[1].ShouldBe(outcomes[0]); logs[1].ShouldBe(logs[0]); logs[0].Count.ShouldBe(shape == 2 ? 3000 : 3001);
        _output.WriteLine($"SET_INNER_ERROR|{shape}|{outcomes[0].Code}|{outcomes[0].Message}|{outcomes[0].Records}|{outcomes[0].Pairs}|{outcomes[0].Sequence}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Inner close failure preserves cancellation or refusal and attaches the cleanup cause (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_InnerCloseFailure_ShouldKeepOriginal(bool cancel)
    {
        // Arrange
        await using var engine = await Create("inner-close", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        using var cancellation = new CancellationTokenSource(); bool injected = false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != "join-inner" || injected) { return; }
            injected = true;
            foreach (var run in StatementRuns(engine.TempSpace!).Where(run => run.IsSealed).ToArray())
            { RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle); }
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw SqlExecutionException.StatementMemoryBudget(262144);
        };
        try
        {
            // Act
            var failure = await Record.ExceptionAsync(async () => await session.ExecuteAsync("SELECT u.id FROM u CROSS JOIN t", cancellationToken: cancellation.Token));
            // Assert
            injected.ShouldBeTrue(); failure.ShouldNotBeNull();
            if (cancel) { failure.ShouldBeAssignableTo<OperationCanceledException>(); }
            else { failure.ShouldBeAssignableTo<DatabaseException>().Diagnostics[0].Code.ShouldBe("CS05067"); }
            WindowFailureData(failure, "InnerCleanup").ShouldBeTrue();
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0); session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Previously refused UNION ALL root and CTAS now keep the base rows (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_SetRootAndTableAs_ShouldComplete(bool tableAs)
    {
        // Arrange
        await using var reference = await Create("set-completion-reference", 67108864);
        await using var spilled = await Create("set-completion-spill", 262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a); await Seed(b);
        string query = tableAs ? "SELECT id FROM t UNION ALL SELECT id FROM t ORDER BY id" : "(SELECT id FROM t ORDER BY k) UNION ALL SELECT id FROM t";
        // Act
        if (tableAs)
        {
            (await a.ExecuteAsync("CREATE TABLE archive AS " + query)).AffectedCount.ShouldBe(8000);
            (await b.ExecuteAsync("CREATE TABLE archive AS " + query)).AffectedCount.ShouldBe(8000);
            query = "SELECT id FROM archive";
        }
        await using var expected = (DatabaseResultSet)await a.ExecuteAsync(query);
        await using var actual = (DatabaseResultSet)await b.ExecuteAsync(query);
        // Assert
        (await Rows(actual)).ShouldBe(await Rows(expected));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Set/inner stage files do not consume the eight client-result slots (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_SetInnerEightResults_ShouldKeepStageCapacity(bool inner)
    {
        // Arrange
        await using var engine = await Create("set-inner-eight", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        var results = new List<DatabaseResultSet>();
        try
        {
            for (int i = 0; i < 8; i++) { results.Add((DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY id")); }
            long before = TempBytes(engine.TempSpace!);
            // Act
            await using var stage = (DatabaseResultSet)await session.ExecuteAsync(inner
                ? "SELECT u.id FROM u JOIN t ON u.id>t.id+100000"
                : "(SELECT id FROM t ORDER BY k) UNION ALL SELECT id FROM t LIMIT 0");
            // Assert
            (await Rows(stage)).ShouldBeEmpty(); TempBytes(engine.TempSpace!).ShouldBe(before);
            (await Rows(results[0])).Length.ShouldBe(4000);
            var ninth = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id,k FROM t ORDER BY id"));
            ninth.Diagnostics[0].Code.ShouldBe("CS05067");
        }
        finally { foreach (var result in results) { await result.DisposeAsync(); } }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Set/inner spills report bytes and runs only after spilling (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_SetInnerExplain_ShouldReportActualSpill(bool inner)
    {
        foreach (long grant in new long[] { 262144, 67108864 })
        {
            // Arrange
            await using var engine = await Create("set-inner-explain-" + grant, grant);
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
            string query = inner ? "SELECT u.id FROM u JOIN t ON u.id>t.id+100000" : "SELECT id FROM t UNION SELECT id FROM t";
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN ANALYZE " + query);
            string description = string.Join('\n', (await Rows(result)).SelectMany(row => row.Cells.OfType<string>()));
            // Assert
            description.Contains("spilled_bytes=", StringComparison.Ordinal).ShouldBe(grant == 262144);
            description.Contains("spill_runs=", StringComparison.Ordinal).ShouldBe(grant == 262144);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Cancellation during new operator stages releases the statement owner (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_SetInnerCancellation_ShouldRelease(bool inner)
    {
        // Arrange
        await using var engine = await Create("set-inner-cancel", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        using var cancellation = new CancellationTokenSource(); bool observed = false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != (inner ? "join-inner" : "set-operation")) { return; }
            observed = true; cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested();
        };
        try
        {
            // Act
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(inner
                ? "SELECT u.id FROM u JOIN t ON u.id>t.id+100000" : "SELECT id FROM t UNION SELECT id FROM t", cancellationToken: cancellation.Token));
            // Assert
            observed.ShouldBeTrue(); session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
        (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Reference-observing catalogs retain the original set/inner refusal without temp I/O (#1789)")]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task ExecuteAsync_SetInnerReferenceGate_ShouldKeepOriginal(bool inner, bool cte)
    {
        // Arrange: even an unused callback can inspect aliases in a later CTE consumer.
        var builder = SqlDatabaseEngine.CreateBuilder("set-inner-reference");
        builder.Options.TempDirectory = Path.Combine(_root, "reference-gate"); builder.Options.StatementMemoryBudget = 262144;
        builder.Functions.Add(SqlScalarFunction.Create<string, string, bool>("same_reference", ReferenceEquals));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        // Act
        string sql = inner ? "SELECT u.id FROM u CROSS JOIN t" : "SELECT id FROM t UNION ALL SELECT id FROM t";
        if (cte) { sql = "WITH c AS (" + sql + ") SELECT * FROM c"; }
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05067");
        SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull();
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unencodable set/inner rows preserve CS05067 and free stage files (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_SetInnerCodec_ShouldKeepOriginal(bool inner)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("set-inner-codec");
        builder.Options.TempDirectory = Path.Combine(_root, "codec-refusal"); builder.Options.StatementMemoryBudget = 262144;
        builder.Functions.Add(SqlScalarFunction.Create("app_binary", (long value) => BitConverter.GetBytes(value)));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(inner
            ? "SELECT u.id FROM u CROSS JOIN (SELECT app_binary(id) AS value FROM t) d"
            : "SELECT app_binary(id) AS value FROM t UNION ALL SELECT app_binary(id) FROM t"));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05067");
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }
}
