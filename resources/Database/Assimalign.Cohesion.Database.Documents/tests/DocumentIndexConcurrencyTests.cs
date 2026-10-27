using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Protocol;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentIndexConcurrencyTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Autocommit index DDL checks schema ownership before document or index conflicts (#1135)")]
    [InlineData("CREATE INDEX added ON items (a)", false)]
    [InlineData("CREATE INDEX added ON items (a)", true)]
    [InlineData("DROP INDEX IF EXISTS ix ON items", false)]
    [InlineData("DROP INDEX IF EXISTS ix ON items", true)]
    public async Task ExecuteAsync_OwnedIndexChangedWhileWaiting_ShouldRefuseOwnershipBeforeConflict(string statement, bool changeIndex)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = DocumentDatabaseEngine.Create("index-ownership-order", new());
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await session.CreateCollectionAsync("items", timeout.Token);
        await session.ExecuteAsync("CREATE INDEX ix ON items (a)", cancellationToken: timeout.Token);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var collection = database.Catalog.FindCollection("items", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(collection with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context, timeout.Token);
        await database.Coordinator.CommitAsync(context, timeout.Token);
        await using var transaction = await writer.BeginTransactionAsync(timeout.Token);
        await ChangeIndexOrDocumentAsync(database, writer, collection.Id, changeIndex, timeout.Token);
        var pending = session.ExecuteAsync(statement, cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        await transaction.CommitAsync(timeout.Token);
        var failure = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await pending.WaitAsync(timeout.Token));

        // Assert
        failure.Message.ShouldContain("Sales");
        failure.Message.ShouldContain(statement.StartsWith("CREATE", StringComparison.Ordinal) ? "CREATE INDEX" : "DROP INDEX");
        session.CurrentTransaction.ShouldBeNull();
        (await session.ExecuteAsync("SELECT * FROM items", cancellationToken: timeout.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
        database.Catalog.GetIndexes(collection.Id, database.LatestSnapshot(context)).Select(index => index.Name)
            .OrderBy(name => name).ShouldBe(changeIndex ? ["concurrent", "ix"] : ["ix"]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Autocommit index DDL checks snapshot conflicts before conditional no-ops (#1135)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ConditionalIndexChangedWhileWaiting_ShouldCheckConflictBeforeNoOp(bool dropOwnedAbsent, bool changeIndex)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = DocumentDatabaseEngine.Create("index-noop-order", new());
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await session.CreateCollectionAsync("items", timeout.Token);
        await session.ExecuteAsync("CREATE INDEX ix ON items (a)", cancellationToken: timeout.Token);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var collection = database.Catalog.FindCollection("items", context.Snapshot).ShouldNotBeNull();
        if (dropOwnedAbsent)
        {
            await database.Catalog.SaveCollectionAsync(collection with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context, timeout.Token);
        }
        await database.Coordinator.CommitAsync(context, timeout.Token);
        await using var transaction = await writer.BeginTransactionAsync(timeout.Token);
        await ChangeIndexOrDocumentAsync(database, writer, collection.Id, changeIndex, timeout.Token);
        string statement = dropOwnedAbsent ? "DROP INDEX IF EXISTS absent ON items" : "CREATE INDEX IF NOT EXISTS ix ON items (a)";
        var pending = session.ExecuteAsync(statement, cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        await transaction.CommitAsync(timeout.Token);
        var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending.WaitAsync(timeout.Token));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD04102");
        session.CurrentTransaction.ShouldBeNull();
        (await session.ExecuteAsync(statement, cancellationToken: timeout.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
    }

    private static async Task ChangeIndexOrDocumentAsync(DocumentDatabase database, DocumentDatabaseSession writer,
        Guid collectionId, bool changeIndex, CancellationToken cancellationToken)
    {
        if (changeIndex)
        {
            await database.RunAsync(writer, async operation =>
            {
                await database.LockWriterAsync(operation.Context, cancellationToken);
                await database.Catalog.CreateIndexAsync(collectionId, "concurrent", "b", operation.Context, cancellationToken);
                return true;
            }, cancellationToken);
        }
        else
        {
            var content = await writer.GetCollectionAsync("items", cancellationToken);
            await content.PutAsync(writer, "changed", "{\"a\":1,\"b\":2}"u8.ToArray(), cancellationToken: cancellationToken);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Waiting writers check latest UNIQUE state and snapshot conflicts (#1135)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutAsync_WaitingWriter_ShouldCheckLatestStateAsync(bool sameDocument)
    {
        // Arrange: exercise real writer-lock contention, with no timing-dependent release.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = DocumentDatabaseEngine.Create("index-concurrency", new());
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.CreateCollectionAsync("items", timeout.Token);
        await first.ExecuteAsync("CREATE UNIQUE INDEX uq ON items (a,b)", cancellationToken: timeout.Token);
        var one = await first.GetCollectionAsync("items", timeout.Token);
        var two = await second.GetCollectionAsync("items", timeout.Token);
        int blocked = 0, refused = 0;
        var elapsed = Stopwatch.StartNew();
        do
        {
            await using var writer = await first.BeginTransactionAsync(timeout.Token);
            await using var stale = await second.BeginTransactionAsync(timeout.Token);
            string id = "one" + blocked;
            await one.PutAsync(first, id, "{\"a\":1,\"b\":2}"u8.ToArray(), cancellationToken: timeout.Token);
            var pending = two.PutAsync(second, sameDocument ? id : "other", "{\"a\":1,\"b\":2}"u8.ToArray(), cancellationToken: timeout.Token).AsTask();
            pending.IsCompleted.ShouldBeFalse();
            blocked++;

            // Act: hold commit after lock release and before coordinator untracking.
            using var gate = new DocumentCommitEventGate(database.Name, writer.Context.Sequence.Value);
            var committing = DedicatedTestTask.Run(async () => await writer.CommitAsync(timeout.Token), timeout.Token);
            try
            {
                await gate.Entered.WaitAsync(timeout.Token);
                database.Coordinator.GetOpenContexts().Any(context => context.Sequence == writer.Context.Sequence &&
                    context.State == TransactionState.Committed).ShouldBeTrue();
                if (sameDocument) { await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending.WaitAsync(timeout.Token)); }
                else { await Should.ThrowAsync<DocumentConstraintViolationException>(async () => await pending.WaitAsync(timeout.Token)); }
                refused++;
            }
            finally { gate.Release(); await committing; }
            gate.Calls.ShouldBe(1);
            gate.TimedOut.ShouldBeFalse();
            await stale.RollbackAsync(timeout.Token);
            await one.DeleteAsync(first, id, cancellationToken: timeout.Token);
        } while ((elapsed.Elapsed < TimeSpan.FromSeconds(1) || blocked < 2) && elapsed.Elapsed < TimeSpan.FromSeconds(20));

        // Assert: every observed contention exercised the refusal path.
        blocked.ShouldBeGreaterThanOrEqualTo(2);
        refused.ShouldBe(blocked);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - A newly committed index aborts a stale writer (#1135)")]
    public async Task PutAsync_IndexSetChanged_ShouldTakeConflictPathAsync()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("index-conflict", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var old = await database.CreateSessionAsync();
        await old.CreateCollectionAsync("items");
        var collection = await old.GetCollectionAsync("items");
        await using var transaction = await old.BeginTransactionAsync();
        await using var ddl = await database.CreateSessionAsync();
        await ddl.ExecuteAsync("CREATE UNIQUE INDEX uq ON items (a,b)");

        // Act / Assert.
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
            await collection.PutAsync(old, "stale", "{\"a\":1,\"b\":2}"u8.ToArray()));
        await transaction.RollbackAsync();
        await collection.PutAsync(old, "fresh", "{\"a\":1,\"b\":2}"u8.ToArray());
    }
}
