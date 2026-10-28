using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentOrderedSnapshotTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Ordered statement snapshots survive concurrent writes and index replacement without holding a latch (#1632)")]
    [InlineData("autocommit", false)]
    [InlineData("autocommit", true)]
    [InlineData("snapshot", false)]
    [InlineData("snapshot", true)]
    [InlineData("read-committed", false)]
    [InlineData("read-committed", true)]
    public async Task Execute_ConcurrentMutations_ShouldKeepStatementSnapshotAndReleaseLatchesAsync(string mode, bool reverse)
    {
        // Arrange: the cursor's first move releases its tree latch before the hook.
        // Waits only guard hangs; observed progress and exact results decide success.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var engine = DocumentDatabaseEngine.Create("ordered-snapshot", new());
        var database = await engine.CreateDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(token);
        await using var writer = await database.CreateSessionAsync(token);
        await writer.CreateCollectionAsync("items", token);
        await writer.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: token);
        for (int i = 0; i < 96; i++)
        { await writer.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('{i:D3}', {{'v':{i + 3}}})", cancellationToken: token); }
        string order = reverse ? "DESC" : "ASC";
        int rounds = 0, mutations = 0;
        var elapsed = Stopwatch.StartNew();
        do
        {
            await writer.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('victim', {'v':1}), ('removed', {'v':2})", cancellationToken: token);
            await writer.ExecuteAsync("DELETE FROM items WHERE META().id = 'inserted'", cancellationToken: token);
            await using var transaction = mode == "autocommit" ? null :
                await session.BeginTransactionAsync(mode == "snapshot" ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted, token);
            var expected = await IdsAsync(session, $"SELECT META().id FROM items ORDER BY CASE WHEN TRUE THEN v ELSE v END {order}", token);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            int callbacks = 0;
            database.Catalog.OrderedIndexEntryRead = () =>
            {
                if (Interlocked.Increment(ref callbacks) != 1) { return; }
                entered.TrySetResult();
                release.Wait(token);
            };
            var pending = Task.Run(() => IdsAsync(session, $"SELECT META().id FROM items ORDER BY v {order}", token), token);
            try
            {
                await entered.Task.WaitAsync(token);

                // Act: committed insert, key-changing update, delete and replacement
                // all finish while the ordered reader is paused between moves.
                await writer.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('inserted', {'v':-1})", cancellationToken: token);
                await writer.ExecuteAsync("UPDATE items SET v = 999 WHERE META().id = 'victim'", cancellationToken: token);
                await writer.ExecuteAsync("DELETE FROM items WHERE META().id = 'removed'", cancellationToken: token);
                await writer.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: token);
                await writer.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: token);
                mutations += 5;
            }
            finally
            {
                database.Catalog.OrderedIndexEntryRead = null;
                release.Set();
            }

            // Assert: original entries and content, including beyond the first batch.
            (await pending.WaitAsync(token)).ShouldBe(expected);
            callbacks.ShouldBeGreaterThanOrEqualTo(1);
            if (mode == "snapshot")
            { (await IdsAsync(session, $"SELECT META().id FROM items ORDER BY v {order}", token)).ShouldBe(expected); }
            if (transaction is not null) { await transaction.CommitAsync(token); }
            var latest = await IdsAsync(session, $"SELECT META().id FROM items ORDER BY v {order}", token);
            latest.ShouldNotContain("removed");
            latest.ShouldContain("inserted");
            latest[reverse ? 0 : latest.Length - 1].ShouldBe("victim");
            rounds++;
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || rounds < 4);
        rounds.ShouldBeGreaterThanOrEqualTo(4);
        mutations.ShouldBe(5 * rounds);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Ordered cursors see own writes and exclude uncommitted or rolled-back writers (#1632)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_TransactionVisibility_ShouldMatchFallbackAsync(bool reverse)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = DocumentDatabaseEngine.Create("ordered-own-writes", new());
        var database = await engine.CreateDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(token);
        await using var writer = await database.CreateSessionAsync(token);
        await session.CreateCollectionAsync("items", token);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {'v':1}), ('b', {'v':2}), ('c', {'v':3})", cancellationToken: token);
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: token);
        string direction = reverse ? "DESC" : "ASC";
        await using (var transaction = await writer.BeginTransactionAsync(token))
        {
            await writer.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('pending', {'v':-1})", cancellationToken: token);
            await writer.ExecuteAsync("UPDATE items SET v = 9 WHERE META().id = 'a'", cancellationToken: token);
            await writer.ExecuteAsync("DELETE FROM items WHERE META().id = 'c'", cancellationToken: token);

            // Act / Assert
            var own = await IdsAsync(writer, $"SELECT META().id FROM items ORDER BY v {direction} LIMIT 2 OFFSET 1", token);
            own.ShouldBe(await IdsAsync(writer, $"SELECT META().id FROM items ORDER BY CASE WHEN TRUE THEN v ELSE v END {direction} LIMIT 2 OFFSET 1", token));
            var other = await IdsAsync(session, $"SELECT META().id FROM items ORDER BY v {direction}", token);
            other.ShouldBe(reverse ? ["c", "b", "a"] : ["a", "b", "c"]);
            await transaction.RollbackAsync(token);
        }
        (await IdsAsync(session, $"SELECT META().id FROM items ORDER BY v {direction}", token)).ShouldBe(reverse ? ["c", "b", "a"] : ["a", "b", "c"]);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Cancellation while completing a tie bucket closes the ordered cursor and permits reuse (#1632)")]
    public async Task Execute_CancelBucket_ShouldReleaseCursorAndStatementAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = DocumentDatabaseEngine.Create("ordered-cancellation", new());
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.CreateCollectionAsync("items", timeout.Token);
        for (int i = 0; i < 96; i++)
        { await session.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('{i:D3}', {{'v':1}})", cancellationToken: timeout.Token); }
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: timeout.Token);
        using var cancellation = new CancellationTokenSource();
        int entries = 0;
        database.Catalog.OrderedIndexEntryRead = () => { if (++entries == 70) { cancellation.Cancel(); } };

        // Act / Assert
        try
        { await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("SELECT META().id FROM items ORDER BY v LIMIT 1", cancellationToken: cancellation.Token)); }
        finally { database.Catalog.OrderedIndexEntryRead = null; }
        entries.ShouldBe(70);
        session.LastQueryDocumentsRead.ShouldBe(0);
        (await IdsAsync(session, "SELECT META().id FROM items ORDER BY v LIMIT 1", timeout.Token)).ShouldBe(["000"]);
    }

    private static async Task<string[]> IdsAsync(DocumentDatabaseSession session, string query, CancellationToken cancellationToken)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: cancellationToken);
        var ids = new List<string>();
        await foreach (var row in result.GetRowsAsync(cancellationToken)) { ids.Add((string)row.GetValue(0)!); }
        return ids.ToArray();
    }
}
