using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentDeleteTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - DELETE: scans and seeks delete the same snapshot and keep index definitions")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_RangePredicate_ShouldRemoveSameDocumentsAndMaintainIndexesAsync(bool indexed)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        if (indexed) { await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None); }
        const string statement = "DELETE FROM items d WHERE d.amount >= 4 AND d.category = 'a'";
        var plan = (await DocumentPlanTestHelper.PlanAsync(database, statement)).ShouldBeOfType<DocumentDeletePlan>();
        if (indexed) { plan.Access.ShouldBeOfType<DocumentIndexPath>().Index.Name.ShouldBe("by_amount"); }
        else { plan.Access.ShouldBeOfType<DocumentScanPath>(); }
        var previous = (await items.GetAsync(session, "b", CancellationToken.None)).ShouldNotBeNull();

        // Act
        var result = await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None);

        // Assert: the residual category predicate keeps c, and non-scalars never match a scalar range.
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        result.ShouldBeOfType<DocumentCommandResult>();
        result.AffectedCount.ShouldBe(1);
        (await IdsAsync(session)).ShouldBe(["a", "c", "d", "object"]);
        (await items.GetAsync(session, "b", CancellationToken.None)).ShouldBeNull();
        (await IdsAsync(session, "WHERE amount >= 4")).ShouldBe(["c"]);
        var replacement = await items.PutAsync(session, "b", previous.Content, cancellationToken: CancellationToken.None);
        replacement.Version.Value.ShouldBeGreaterThan(previous.Version.Value);
        (await IdsAsync(session, "WHERE amount >= 4")).ShouldBe(["b", "c"]);
        (await session.ExecuteAsync("DELETE FROM items", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(5);
        (await IdsAsync(session)).ShouldBeEmpty();
        (await session.GetCollectionAsync("items", CancellationToken.None)).Name.ShouldBe("items");
        var indexes = await ValuesAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES");
        indexes.ShouldBe(indexed ? new object?[] { "by_amount" } : []);
        (await session.ExecuteAsync("DELETE FROM items", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DELETE: parameter, reversed and BETWEEN bounds reuse existing seeks")]
    [InlineData("amount >= $minimum")]
    [InlineData("$minimum <= d.amount")]
    [InlineData("d.amount BETWEEN $minimum AND 6")]
    [InlineData("d.amount = 4")]
    public async Task Plan_IndexedBounds_ShouldChooseSeekAndReapplyPredicateAsync(string predicate)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["minimum"] = 4 };
        string statement = "DELETE FROM items d WHERE " + predicate;

        // Act / Assert
        (await DocumentPlanTestHelper.PlanAsync(database, statement, parameters)).ShouldBeOfType<DocumentDeletePlan>()
            .Access.ShouldBeOfType<DocumentIndexPath>();
        var result = await session.ExecuteAsync(statement, parameters, CancellationToken.None);
        result.AffectedCount.ShouldBe(predicate.EndsWith("= 4", StringComparison.Ordinal) ? 1 : 2);
        (await IdsAsync(session)).ShouldContain("object");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DELETE: META identity remains a residual scan with literals or parameters")]
    [InlineData("'b'")]
    [InlineData("$id")]
    public async Task Execute_MetaPredicate_ShouldScanAndDeleteOnlyExternalIdentityAsync(string key)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_id ON items (id)", cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["id"] = "b" };
        string statement = $"DELETE FROM items d WHERE META(d).id = {key}";

        // Act / Assert
        (await DocumentPlanTestHelper.PlanAsync(database, statement, parameters)).ShouldBeOfType<DocumentDeletePlan>()
            .Access.ShouldBeOfType<DocumentScanPath>();
        (await session.ExecuteAsync(statement, parameters, CancellationToken.None)).AffectedCount.ShouldBe(1);
        (await IdsAsync(session)).ShouldBe(["a", "c", "d", "object"]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DELETE: WHERE shares SQL++ Truth conversion")]
    [InlineData("1", 5)]
    [InlineData("NULL", 0)]
    [InlineData("MISSING", 0)]
    [InlineData("absent", 0)]
    [InlineData("amount > 100", 0)]
    [InlineData("amount = {'score': 5}", 1)]
    public async Task Execute_TruthPredicate_ShouldDeleteOnlyTrueValuesAsync(string predicate, int expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await session.ExecuteAsync("DELETE FROM items WHERE " + predicate, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(expected);
        (await IdsAsync(session)).Length.ShouldBe(5 - expected);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - DELETE: rollback restores documents and indexes while an older snapshot retains them")]
    public async Task Execute_ExplicitTransaction_ShouldRespectRollbackAndOlderSnapshotsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None);
        await using var observer = await database.CreateSessionAsync(CancellationToken.None);
        await using var snapshot = await observer.BeginTransactionAsync(CancellationToken.None);

        // Act / Assert
        await using (var transaction = await session.BeginTransactionAsync(CancellationToken.None))
        {
            (await session.ExecuteAsync("DELETE FROM items WHERE amount >= 4", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
            (await IdsAsync(session, "WHERE amount >= 4")).ShouldBeEmpty();
            (await IdsAsync(observer, "WHERE amount >= 4")).ShouldBe(["b", "c"]);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await IdsAsync(session, "WHERE amount >= 4")).ShouldBe(["b", "c"]);
        await session.ExecuteAsync("DELETE FROM items WHERE amount >= 4", cancellationToken: CancellationToken.None);
        (await IdsAsync(session, "WHERE amount >= 4")).ShouldBeEmpty();
        (await IdsAsync(observer, "WHERE amount >= 4")).ShouldBe(["b", "c"]);
        await snapshot.RollbackAsync(CancellationToken.None);
        (await IdsAsync(observer, "WHERE amount >= 4")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DELETE: a later matched-key conflict rolls back every earlier deletion")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ConcurrentMatchedWrite_ShouldAbortWholeStatementOrTransactionAsync(bool explicitTransaction)
    {
        // Arrange: the blocker holds the writer lock until DELETE has materialized its candidates.
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None);
        await using var blocker = await database.CreateSessionAsync(CancellationToken.None);
        var blockedItems = await blocker.GetCollectionAsync("items", CancellationToken.None);
        await using var blocking = await blocker.BeginTransactionAsync(CancellationToken.None);
        await blockedItems.PutAsync(blocker, "b", "{\"amount\":8}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = session.ExecuteAsync("DELETE FROM items WHERE amount >= 2", cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act: a deletes first; b's latest version then conflicts with DELETE's snapshot.
        await blocking.CommitAsync(timeout.Token);
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);

        // Assert
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM items", cancellationToken: timeout.Token));
            await transaction.RollbackAsync(timeout.Token);
        }
        (await IdsAsync(session)).ShouldBe(["a", "b", "c", "d", "object"]);
        (await IdsAsync(session, "WHERE amount >= 2")).ShouldBe(["a", "b", "c"]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DELETE: cancellation while waiting for the writer leaves every document present")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CancellationDuringStatement_ShouldDeleteNothingAsync(bool explicitTransaction)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await using var blocker = await database.CreateSessionAsync(CancellationToken.None);
        var blockedItems = await blocker.GetCollectionAsync("items", CancellationToken.None);
        await using var blocking = await blocker.BeginTransactionAsync(CancellationToken.None);
        await blockedItems.PutAsync(blocker, "blocker", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = session.ExecuteAsync("DELETE FROM items", cancellationToken: source.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        source.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);

        // Assert
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        await blocking.RollbackAsync(CancellationToken.None);
        (await IdsAsync(session)).ShouldBe(["a", "b", "c", "d", "object"]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DELETE: a predicate failure aborts every write of its transaction")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_LaterPredicateFailure_ShouldPreserveContentAndAbortPriorWorkAsync(bool explicitTransaction)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        if (transaction is not null)
        {
            await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('prior', {'amount':9})", cancellationToken: CancellationToken.None);
        }

        // Act: a matches; b's predicate fails before the collected ids may be deleted.
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "DELETE FROM items WHERE CASE WHEN META().id = 'b' THEN 1 / 0 ELSE 1 END", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02006");
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await IdsAsync(session)).ShouldBe(["a", "b", "c", "d", "object"]);
        (await IdsAsync(session, "WHERE amount >= 2")).ShouldBe(["a", "b", "c"]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DELETE: invalid predicates are validated even over empty collections")]
    [InlineData("DELETE FROM items WHERE 0 AND $absent", "CD01005")]
    [InlineData("DELETE FROM items d WHERE META(other).id = 'b'", "CD05001")]
    [InlineData("DELETE FROM items WHERE COUNT(*) > 0", "CD05001")]
    [InlineData("DELETE FROM absent", "CD01002")]
    public async Task Execute_InvalidContext_ShouldRejectBeforeMutationAsync(string statement, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        if (code == "CD01002") { error.Message.ShouldBe("Collection 'absent' does not exist."); }
        (await session.ExecuteAsync("DELETE FROM items", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - DELETE: schema ownership permits content mutation")]
    public async Task Execute_SchemaOwnedCollection_ShouldDeleteContentAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("delete", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var metadata = database.Catalog.FindCollection("items", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(metadata with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context, CancellationToken.None);
        await database.Coordinator.CommitAsync(context, CancellationToken.None);

        // Act / Assert
        (await session.ExecuteAsync("DELETE FROM items", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(5);
        (await IdsAsync(session)).ShouldBeEmpty();
        (await session.GetCollectionAsync("items", CancellationToken.None)).Name.ShouldBe("items");
    }

    private static async Task<DocumentCollection> SeedAsync(DocumentDatabaseSession session)
    {
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        (string Id, string Content)[] documents =
        [
            ("a", """{"name":"alpha","category":"a","amount":2}"""),
            ("b", """{"name":"beta","category":"a","amount":6}"""),
            ("c", """{"name":"gamma","category":"b","amount":4}"""),
            ("d", """{"name":"delta","category":"a","amount":null}"""),
            ("object", """{"amount":{"score":5},"id":"b"}"""),
        ];
        foreach (var (id, content) in documents)
        { await collection.PutAsync(session, id, Encoding.UTF8.GetBytes(content), cancellationToken: CancellationToken.None); }
        return collection;
    }

    private static async Task<string[]> IdsAsync(DocumentDatabaseSession session, string filter = "")
        => (await ValuesAsync(session, "SELECT META().id FROM items " + filter)).Cast<string>().ToArray();

    private static async Task<object?[]> ValuesAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        return values.ToArray();
    }
}
