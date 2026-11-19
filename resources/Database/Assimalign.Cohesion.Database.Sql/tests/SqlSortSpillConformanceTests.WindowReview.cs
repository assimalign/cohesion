using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Window record capacity is distinct from codec eligibility (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Append_WindowRecordCapacity_ShouldIdentifyOnlyCodecFailure(bool unencodable)
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-record-capacity", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        using var spool = new SqlWindowInputSpool(new SqlStatementMetrics(), 262144, session, CancellationToken.None);
        using var statement = spool.Statement;
        object?[] row = [unencodable ? new object() : new string('x', 17000)];

        // Act
        var failure = Should.Throw<SqlExecutionException>(() => spool.Append(row, CancellationToken.None));

        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05067");
        failure.Data.Contains(nameof(SqlTempRowCodec)).ShouldBe(unencodable);
        statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window append refuses an unreadable row before writing any record (#1789)")]
    public async Task Append_WindowUnreadableRow_ShouldRefuseBeforeWriting()
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-append-admission", 4096);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics();
        using var spool = new SqlWindowInputSpool(metrics, 4096, session, CancellationToken.None);
        using var statement = spool.Statement;
        // Act
        var failure = Record.Exception(() => spool.Append([new string('x', 2000)], CancellationToken.None));
        // Assert
        _output.WriteLine($"WINDOW_APPEND|records={StatementRuns(engine.TempSpace!).Single().RecordCount}|failure={failure?.GetType().Name}");
        failure.ShouldBeOfType<SqlExecutionException>().Diagnostics[0].Code.ShouldBe("CS05067");
        StatementRuns(engine.TempSpace!).Single().RecordCount.ShouldBe(0);
        statement.PhysicalBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Reused window append scratch preserves exact tuple bytes and round trips (#1789)")]
    public async Task Append_WindowScratch_ShouldPreserveEncodedBytes()
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-append-bytes", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics();
        using var spool = new SqlWindowInputSpool(metrics, 262144, session, CancellationToken.None);
        using var statement = spool.Statement;
        object?[][] input = [[1L, "A\0é\U0001f600", new byte[] { 0, 128, 255 }, -1.2300m, null], [2L, new string('x', 4000), true]];
        var expected = input.Select(row => { SqlTempRowCodec.TryEncode(row, out var bytes).ShouldBeTrue(); return bytes; }).ToArray();
        // Act
        foreach (var row in input) { spool.Append(row, CancellationToken.None); }
        spool.Seal(CancellationToken.None);
        // Assert: inspect the wire bytes before consuming the one-shot stage reader.
        var run = StatementRuns(engine.TempSpace!).Single();
        using (var reader = run.OpenReader(StorageTempSpace.PageSize))
        {
            var scratch = new byte[16384];
            foreach (var bytes in expected)
            { reader.TryRead(scratch, out int length).ShouldBeTrue(); scratch.AsSpan(0, length).ToArray().ShouldBe(bytes); }
        }
        var decoded = spool.Read(CancellationToken.None).ToArray();
        for (int i = 0; i < decoded.Length; i++)
        { SqlTempRowCodec.TryEncode(decoded[i], out var bytes).ShouldBeTrue(); bytes.ShouldBe(expected[i]); }
        decoded.Length.ShouldBe(input.Length); metrics.HashMemoryBytes.ShouldBe(0);
        statement.PhysicalBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window codec failures preserve the base refusal and fitting binary results (#1789)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ExecuteAsync_WindowBinaryCodec_ShouldKeepBaseRefusal(int shape)
    {
        // Arrange: BIGINT parameters admit these callbacks; their binary outputs cannot spool.
        string sql = shape switch
        {
            0 => "SELECT id,ROW_NUMBER() OVER(PARTITION BY app_bin(id%100) ORDER BY id) FROM t",
            1 => "SELECT id,FIRST_VALUE(app_bin(id)) OVER(PARTITION BY id%100 ORDER BY id) FROM t",
            _ => "SELECT id,app_agg(id) OVER(PARTITION BY id%100 ORDER BY id) FROM t GROUP BY id",
        };
        var errors = new List<string>();
        int codecRefusals = 0, completions = 0;
        foreach (long grant in new long[] { 262144, 524288, 786432, 1048576, 2097152, 67108864 })
        {
            var outcomes = new List<(string Code, RowValues[] Rows, Exception? Failure)>();
            foreach (bool reference in new[] { true, false })
            {
                var builder = SqlDatabaseEngine.CreateBuilder("window-codec");
                builder.Options.TempDirectory = System.IO.Path.Combine(_root, $"codec-{shape}-{grant}-{reference}");
                builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 134217728;
                builder.Functions.Add(SqlScalarFunction.Create("app_bin", static (long value) => BitConverter.GetBytes(value)));
                builder.Functions.Add(SqlAggregateFunction.Create<long, long, byte[]>("app_agg", static () => 0,
                    static (state, value) => state + value, static state => BitConverter.GetBytes(state)));
                await using var engine = await builder.BuildAsync();
                var database = await engine.CreateDatabaseAsync("db"); database.Definitions.ForceSelectRetentionBasePath = reference;
                await using var session = await database.CreateSessionAsync(); await Seed(session);
                // Act
                try
                {
                    await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
                    outcomes.Add(("ok", await Rows(result), null));
                }
                catch (DatabaseException failure) { outcomes.Add((failure.Diagnostics[0].Code!, [], failure)); }
                TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            }
            // Assert: collect every diagnostic before failing a reproduction run.
            var before = outcomes[0]; var after = outcomes[1];
            bool codec = after.Failure is not null && WindowFailureData(after.Failure, nameof(SqlTempRowCodec));
            _output.WriteLine($"WINDOW_CODEC|shape={shape}|G={grant}|base={before.Code}|head={after.Code}|codec={codec}");
            if (after.Code != "ok" && after.Code != before.Code) { errors.Add($"{grant}: {before.Code} -> {after.Code}"); }
            if (before.Code == "ok") { after.Code.ShouldBe("ok"); after.Rows.ShouldBe(before.Rows); completions++; }
            if (codec)
            {
                after.Code.ShouldBe(before.Code);
                if (before.Code == "CS05079") { codecRefusals++; }
            }
        }
        errors.ShouldBeEmpty(); completions.ShouldBeGreaterThan(0);
        if (Environment.GetEnvironmentVariable("SQ_L4B_RECORD") is null) { codecRefusals.ShouldBeGreaterThan(0); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Key sort close failure keeps cancellation or the base refusal and its cleanup cause (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_WindowSortCloseFailure_ShouldKeepOriginal(bool cancel)
    {
        // Arrange: inject after the key sorter has flushed a run, before Complete.
        const long grant = 262144;
        await using var engine = await CreateWindowAsync("window-sort-close", grant);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        using var cancellation = new CancellationTokenSource();
        var inputRuns = new HashSet<StorageTempRun>();
        int seals = 0, decoded = 0; bool injected = false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase == "window-input" && ++seals == 2) { inputRuns.UnionWith(StatementRuns(engine.TempSpace!)); }
        };
        SqlRowCollector.ObserveBuffer = (phase, _) =>
        {
            if (phase != "window-decoded" || seals != 2 || ++decoded < 3000 || injected) { return; }
            var run = StatementRuns(engine.TempSpace!).FirstOrDefault(run => run.IsSealed && !inputRuns.Contains(run));
            if (run is null) { return; }
            injected = true; RunFile(run) = new FailedCloseStream(RunFile(run).SafeFileHandle);
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw SqlExecutionException.StatementMemoryBudget(grant);
        };
        try
        {
            // Act
            var failure = await Record.ExceptionAsync(async () => await session.ExecuteAsync(
                "SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY id) FROM t", cancellationToken: cancellation.Token));
            // Assert
            injected.ShouldBeTrue(); failure.ShouldNotBeNull();
            _output.WriteLine($"WINDOW_CLOSE|cancel={cancel}|type={failure.GetType().Name}|code={(failure as DatabaseException)?.Diagnostics.FirstOrDefault()?.Code}");
            if (cancel) { failure.ShouldBeAssignableTo<OperationCanceledException>(); }
            else { failure.ShouldBeAssignableTo<DatabaseException>().Diagnostics[0].Code.ShouldBe("CS05079"); }
            WindowFailureData(failure, nameof(StorageTempSorter)).ShouldBeTrue();
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; SqlRowCollector.ObserveBuffer = null; }
    }

    private static bool WindowFailureData(Exception failure, string key)
    {
        if (failure.Data.Contains(key)) { return true; }
        foreach (DictionaryEntry item in failure.Data)
        { if (item.Value is Exception nested && WindowFailureData(nested, key)) { return true; } }
        if (failure is AggregateException aggregate && aggregate.InnerExceptions.Any(item => WindowFailureData(item, key))) { return true; }
        return failure.InnerException is { } inner && WindowFailureData(inner, key);
    }
}
