using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentLikeCancellationCollection))]
public sealed class DocumentWindowBudgetTests
{
    private readonly ITestOutputHelper _output;
    public DocumentWindowBudgetTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Documents] - Windows: stage reservations unwind on success budget work and cancellation")]
    [InlineData("success")]
    [InlineData("memory")]
    [InlineData("work")]
    [InlineData("cancel")]
    public async Task Execute_WindowLifetime_ShouldReleaseEveryReservationAsync(string scenario)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var builder = DocumentDatabaseEngine.CreateBuilder("window-budget");
        builder.Options.StatementMemoryBudget = scenario == "memory" ? 24_000 : 1024 * 1024;
        builder.Options.StatementWorkBudget = scenario == "work" ? 400 : 100_000;
        int keys = 0;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("key_value", value =>
        { keys++; if (scenario == "cancel" && keys == 16) { cancellation.Cancel(); } return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var input = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 64)) + "]");
        var parameters = new Dictionary<string, object?> { ["rows"] = input.RootElement };
        var request = DocumentQueryRequest.FromOql("SELECT ROW_NUMBER() OVER (ORDER BY key_value(x)) AS n FROM @rows AS x LIMIT 1");
        // Act / Assert: inspect the operation counter before its final cleanup can hide a leak.
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(cancellation.Token);
            if (scenario == "success")
            {
                await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, cancellation.Token))
                {
                    int count = 0;
                    await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetInt64(0).ShouldBe(1); count++; }
                    count.ShouldBe(1);
                    memory.Bytes.ShouldBe(320); // Only one scalar result row remains.
                }
                keys.ShouldBe(64); // LIMIT did not truncate the partition.
            }
            else if (scenario == "cancel")
            {
                await Should.ThrowAsync<OperationCanceledException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, cancellation.Token));
                keys.ShouldBe(16);
            }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, cancellation.Token));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(scenario == "memory" ? "CD05018" : "CD05020");
            }
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Windows: completed statements retain no navigation values at either row count")]
    [InlineData(256)]
    [InlineData(2048)]
    public async Task Execute_CompletedWindow_ShouldReleaseLiveObjectsAsync(int count)
    {
        // Arrange
        var values = new List<WeakReference>();
        var builder = DocumentDatabaseEngine.CreateBuilder("window-live");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("payload", n =>
        {
            var value = new string((char)('a' + (int)n % 26), 512);
            values.Add(new WeakReference(value));
            return value;
        }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        long before = LiveHeap.Read();
        // Act
        await ExecuteAndDisposeAsync(session, count);
        long after = LiveHeap.Read();
        // Assert: the GC metric is portable; object identity is the release assertion.
        values.Count.ShouldBe(count - 1); // LEAD never evaluates the unused first target value.
        values.Count(value => value.IsAlive).ShouldBe(0);
        _output.WriteLine($"Window rows={count}; live heap before={before}; after={after}; navigation objects alive=0.");
    }


    [Fact(DisplayName = "Cohesion Test [Documents] - Windows: stage charges exceed an equal-projection control (#1805)")]
    public async Task Execute_WindowStage_ShouldChargeAdditionalRetainedMemoryAsync()
    {
        // Arrange
        const int count = 64;
        await using var engine = DocumentDatabaseEngine.Create("window-charge", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var input = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, count)) + "]");
        var parameters = new Dictionary<string, object?> { ["rows"] = input.RootElement };
        // Act
        long control = await PeakAsync("SELECT x + 2 AS n FROM @rows AS x WHERE EXISTS (SELECT RAW 1 FROM [0] AS z) ORDER BY x LIMIT 1");
        long window = await PeakAsync("SELECT ROW_NUMBER() OVER () + RANK() OVER () AS n FROM @rows AS x WHERE EXISTS (SELECT RAW 1 FROM [0] AS z) ORDER BY x LIMIT 1");
        // Assert: row staging + result maps + sort workspace must all be owned.
        _output.WriteLine($"Control peak={control}; window peak={window}; extra={window - control}");
        (window - control).ShouldBeGreaterThanOrEqualTo(count * (192L + 288L + 128L));

        async Task<long> PeakAsync(string text)
        {
            var request = DocumentQueryRequest.FromOql(text);
            return await database.RunAsync(session, async operation =>
            {
                var memory = operation.GetStatementMemory(CancellationToken.None);
                await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation,
                    request.Statement, parameters, CancellationToken.None))
                {
                    int rows = 0;
                    await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }
                    rows.ShouldBe(1);
                }
                memory.Bytes.ShouldBe(0);
                return memory.PeakBytes;
            }, CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Windows: key-free specifications skip comparison work (#1805)")]
    public async Task Execute_KeyFreeWindow_ShouldFitLinearWorkBudgetAsync()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("window-no-sort");
        builder.Options.StatementWorkBudget = 400;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var input = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 64)) + "]");
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            "SELECT ROW_NUMBER() OVER () AS n FROM @rows AS x LIMIT 1",
            new Dictionary<string, object?> { ["rows"] = input.RootElement }, CancellationToken.None);
        // Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetInt64(0).ShouldBe(1); count++; }
        count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Windows: navigation retains each value once (#1805)")]
    public async Task Execute_NavigationValues_ShouldRetainOnceAsync()
    {
        // Arrange
        const int count = 64, length = 512;
        await using var engine = DocumentDatabaseEngine.Create("window-navigation-charge", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var input = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, count)) + "]");
        var parameters = new Dictionary<string, object?> { ["rows"] = input.RootElement };
        // Act
        long ranks = await PeakAsync("ROW_NUMBER()");
        long navigation = await PeakAsync("LAG('" + new string('a', length) + "',0)");
        // Assert: the value's 96 + 2*length reservation replaces a 96-byte rank value.
        // Allow a complete additional output cell, independently of the retained stage values.
        _output.WriteLine($"Rank peak={ranks}; navigation peak={navigation}; extra={navigation-ranks}");
        (navigation - ranks).ShouldBeLessThanOrEqualTo(count * (96L + 2L * length) + 2048L);

        async Task<long> PeakAsync(string call)
        {
            var request = DocumentQueryRequest.FromOql("SELECT " + call + " OVER () IS NOT NULL AS n FROM @rows AS x LIMIT 1");
            return await database.RunAsync(session, async operation =>
            {
                var memory = operation.GetStatementMemory(CancellationToken.None);
                await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation,
                    request.Statement, parameters, CancellationToken.None))
                { await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldNotBeNull(); } }
                memory.Bytes.ShouldBe(0);
                return memory.PeakBytes;
            }, CancellationToken.None);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ExecuteAndDisposeAsync(DocumentDatabaseSession session, int count)
    {
        using var input = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, count)) + "]");
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            "SELECT LEAD(payload(x)) OVER (ORDER BY x) AS next FROM @rows AS x ORDER BY x LIMIT 1",
            new Dictionary<string, object?> { ["rows"] = input.RootElement }, CancellationToken.None);
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetString(0).ShouldNotBeNull().Length.ShouldBe(512); }
    }
}
