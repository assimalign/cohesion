using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlIndexPruneTests
{
    private readonly ITestOutputHelper _output;
    public SqlIndexPruneTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Index prune: the purge pass preserves an old snapshot then removes its dead index versions (#1195)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunVersionPurgePass_OldSnapshotEnds_ShouldRemoveDeadEntriesAndKeepItsViewUntilThen(bool olderWriterActive)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("index-prune", QuietOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var writer = await database.CreateSessionAsync();
        await writer.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, n INT)");
        await writer.ExecuteAsync("INSERT INTO t VALUES (2, 0)");
        await using var firstWriter = olderWriterActive ? await writer.BeginTransactionAsync(IsolationLevel.Snapshot) : null;
        if (olderWriterActive) { await writer.ExecuteAsync("UPDATE t SET n = 1 WHERE id = 2", cancellationToken: Guard()); }
        await using var old = await database.CreateSessionAsync();
        await using var transaction = await old.BeginTransactionAsync(IsolationLevel.Snapshot);
        (await ScalarAsync(old)).ShouldBe(0L);
        if (firstWriter is not null) { await firstWriter.CommitAsync(); }
        for (int update = olderWriterActive ? 1 : 0; update < 1_000; update++)
        {
            await writer.ExecuteAsync("UPDATE t SET n = n + 1 WHERE id = 2", cancellationToken: Guard());
        }

        // Act / Assert: no horizon may pass the held snapshot.
        database.Coordinator.RunVersionPurgePass(Guard());
        (await ScalarAsync(old)).ShouldBe(0L);
        (await ScalarAsync(writer)).ShouldBe(1_000L);
        (await EntriesExaminedAsync(database)).ShouldBe(1_001);
        await transaction.RollbackAsync();

        database.Coordinator.RunVersionPurgePass(Guard());
        (await EntriesExaminedAsync(database)).ShouldBe(1);
        (await ScalarAsync(writer)).ShouldBe(1_000L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Index prune: 20,000 hot UNIQUE updates keep physical work flat (#1195)")]
    public async Task ExecuteAsync_TwentyThousandHotUpdates_ShouldKeepIndexWorkFlat()
    {
        // Arrange: maintenance is disabled; opportunistic pruning must bound the hot walk.
        await using var engine = SqlDatabaseEngine.Create("index-hot-key", QuietOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, n INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (2, 0)");
        const int updates = 20_000;
        const int window = 256;
        long early = 0;
        long late = 0;

        // Act: count actual physical entry-directory reads, without a duration assertion.
        try
        {
            SetEntryAccessed(null, () => Interlocked.Increment(ref early));
            for (int update = 0; update < updates; update++)
            {
                if (update == window) { SetEntryAccessed(null, null); }
                if (update == updates - window) { SetEntryAccessed(null, () => Interlocked.Increment(ref late)); }
                await session.ExecuteAsync("UPDATE t SET n = n + 1 WHERE id = 2", cancellationToken: Guard());
            }
        }
        finally { SetEntryAccessed(null, null); }

        // Assert
        _output.WriteLine($"Physical reads/update: first {early / (double)window:F4}, last {late / (double)window:F4}, growth {late / (double)early:F4}.");
        early.ShouldBeGreaterThan(0);
        late.ShouldBeGreaterThan(0);
        (late / (double)early).ShouldBeLessThan(2);
        (await ScalarAsync(session)).ShouldBe((long)updates);
    }

    private static async Task<long> EntriesExaminedAsync(SqlDatabase database)
    {
        var registration = database.IndexManager.ExportRegistrations().Single();
        database.IndexManager.TryGetIndex(registration.ObjectId, registration.Definition.Name, out var index).ShouldBeTrue();
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, Guard());
        try
        {
            await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);
            int visible = 0;
            while (await cursor.MoveNextAsync(Guard())) { visible++; }
            visible.ShouldBe(1);
            return EntriesExamined(cursor);
        }
        finally { await database.Coordinator.RollbackAsync(reader, Guard()); }
    }

    private static async Task<object?> ScalarAsync(SqlDatabaseSession session)
    {
        await using var result = (await session.ExecuteAsync("SELECT n FROM t WHERE id = 2", cancellationToken: Guard())).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in result.GetRowsAsync()) { return row.GetValue(0); }
        throw new InvalidOperationException("The hot row is missing.");
    }

    private static SqlDatabaseEngineOptions QuietOptions() => new()
    {
        MaintenanceInterval = TimeSpan.FromHours(1),
        CheckpointInterval = TimeSpan.FromHours(1),
    };

    // Five-minute hang guard only; no elapsed duration decides either work assertion.
    private static CancellationToken Guard() => TestTimeout.Token(300);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EntriesExamined")]
    private static extern long EntriesExamined(BTreeCursor cursor);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "set_EntryAccessed")]
    private static extern void SetEntryAccessed(BTreeIndex? declaringType, Action? observer);
}
