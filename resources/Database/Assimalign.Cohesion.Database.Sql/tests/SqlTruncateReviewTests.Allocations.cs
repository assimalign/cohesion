#if !DEBUG
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTruncateReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - TRUNCATE: join retirement checks allocate no additional memory (#1716)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsTruncatedTable_JoinBindings_ShouldAllocateNothing(bool truncate)
    {
        // Arrange: use the same two owner descriptions that the joined snapshot binds.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-join-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE p (id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(writer, "CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id))", timeout.Token);
        await ExecAsync(writer, "INSERT INTO p VALUES (1)", timeout.Token);
        await ExecAsync(writer, "INSERT INTO c VALUES (2,1)", timeout.Token);
        await using var pin = await reader.BeginTransactionAsync(timeout.Token);
        var tables = database.Catalog.Tables.ToArray();
        if (truncate) { await ExecAsync(writer, "TRUNCATE p CASCADE", timeout.Token); }
        (await RowsAsync(reader, "SELECT c.id, p.id FROM c JOIN p ON c.pid=p.id", timeout.Token))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 2, 1 });
        for (int i = 0; i < 100; i++) { foreach (var table in tables) { database.Catalog.IsTruncatedTable(table).ShouldBe(truncate); } }

        // Act: isolate the extra per-binding check from the join's existing buffers and locks.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { foreach (var table in tables) { _ = database.Catalog.IsTruncatedTable(table); } }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"retired={truncate}; join retirement check bytes/1000={allocated}");

        // Assert
        allocated.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - TRUNCATE: ordered retirement maintenance keeps one array per append and removal (#1716)")]
    public async Task CompleteTableReclamationAsync_ManyRetirements_ShouldKeepLinearAllocationBudget()
    {
        // Arrange: pin all owners so the background worker cannot remove them during measurement.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("truncate-ordered-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(writer, "CREATE TABLE t (id INT)", timeout.Token);
        await using var pin = await reader.BeginTransactionAsync(timeout.Token);
        for (int i = 0; i < 100; i++) { await ExecAsync(writer, "TRUNCATE t", timeout.Token); }
        var owners = database.Catalog.GetPendingTableReclamations().ToArray();

        // Act: completion is catalog-only here; no reader accesses the test-retired owners afterwards.
        long append = Measure(() =>
        {
            var pending = ExecAsync(writer, "TRUNCATE t", timeout.Token);
            if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("The measured publication did not complete synchronously."); }
            pending.GetAwaiter().GetResult();
        });
        long remove = Measure(() =>
        {
            var pending = database.Catalog.CompleteTableReclamationAsync(owners[50], timeout.Token);
            if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("The measured completion did not complete synchronously."); }
            pending.GetAwaiter().GetResult();
        });
        _output.WriteLine($"100 retirements: append={append}; middle removal={remove}");

        // Assert
        // The former full sort used 35,440 and 8,440 bytes respectively on this fixture.
        // These caps admit the copied owner array without sort keys/permutations/buffers.
        append.ShouldBeLessThanOrEqualTo(32_000L);
        remove.ShouldBeLessThanOrEqualTo(4_000L);
        database.Catalog.GetPendingTableReclamations().Count.ShouldBe(100);

        static long Measure(Action action)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
#endif
