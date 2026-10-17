using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentCollectionDdlTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Collection DDL: C# and OQL share errors and zero-count command results")]
    public async Task Execute_CollectionLifecycle_ShouldMatchSessionApiAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        Success(await session.ExecuteAsync("CREATE COLLECTION x", cancellationToken: CancellationToken.None));
        var metadata = await CollectionsAsync(session);
        (await session.GetCollectionAsync("x", CancellationToken.None)).Name.ShouldBe("x");
        (await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("CREATE COLLECTION x", cancellationToken: CancellationToken.None)))
            .Message.ShouldBe("Collection 'x' already exists.");
        (await Should.ThrowAsync<DatabaseException>(async () =>
            await session.CreateCollectionAsync("x", CancellationToken.None))).Message.ShouldBe("Collection 'x' already exists.");
        Success(await session.ExecuteAsync("CREATE COLLECTION IF NOT EXISTS x", cancellationToken: CancellationToken.None));
        Success(await session.ExecuteAsync("DROP COLLECTION IF EXISTS absent", cancellationToken: CancellationToken.None));
        (await CollectionsAsync(session)).ShouldBe(metadata);
        await session.DropCollectionAsync("x", CancellationToken.None);
        (await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("DROP COLLECTION x", cancellationToken: CancellationToken.None)))
            .Message.ShouldBe("Collection 'x' does not exist.");
        (await Should.ThrowAsync<DatabaseException>(async () =>
            await session.DropCollectionAsync("x", CancellationToken.None))).Message.ShouldBe("Collection 'x' does not exist.");
        await session.CreateCollectionAsync("x", CancellationToken.None);
        Success(await session.ExecuteAsync("DROP COLLECTION x", cancellationToken: CancellationToken.None));
        (await CollectionsAsync(session)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Collection DDL: system names are read-only in every spelling")]
    [InlineData("CREATE COLLECTION COHESION_SCHEMA.INDEXES")]
    [InlineData("CREATE COLLECTION \"COHESION_SCHEMA.INDEXES\"")]
    [InlineData("CREATE COLLECTION IF NOT EXISTS cohesion_schema.indexes")]
    [InlineData("DROP COLLECTION COHESION_SCHEMA.INDEXES")]
    [InlineData("DROP COLLECTION IF EXISTS \"cohesion_schema.indexes\"")]
    public async Task Execute_SystemCollection_ShouldRefuseBeforeLookupAsync(string statement)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        // Assert
        error.Message.ShouldBe("System collection 'COHESION_SCHEMA.INDEXES' is read-only.");
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC004");
        diagnostic.Start.ShouldBe(0);
        diagnostic.End.ShouldBe(statement.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Collection DDL: conditional creation preserves schema ownership and drops remain locked")]
    public async Task Execute_SchemaOwnedCollection_ShouldPreserveAuthorityAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("owned", CancellationToken.None);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var metadata = database.Catalog.FindCollection("owned", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(metadata with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context, CancellationToken.None);
        await database.Coordinator.CommitAsync(context, CancellationToken.None);

        // Act / Assert
        var before = await CollectionsAsync(session);
        Success(await session.ExecuteAsync("CREATE COLLECTION IF NOT EXISTS owned", cancellationToken: CancellationToken.None));
        (await CollectionsAsync(session)).ShouldBe(before);
        foreach (string statement in new[] { "DROP COLLECTION owned", "DROP COLLECTION IF EXISTS owned" })
        {
            var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
                await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));
            error.Operation.ShouldBe("DROP COLLECTION");
            error.OwningSchema.ShouldBe("Sales");
        }
        (await CollectionsAsync(session)).ShouldBe(before);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Collection DDL: CREATE is visible inside its transaction and rolls back")]
    public async Task Execute_CreateInTransaction_ShouldRollbackCatalogAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);

        // Act
        Success(await session.ExecuteAsync("CREATE COLLECTION new_collection", cancellationToken: CancellationToken.None));

        // Assert
        (await CollectionsAsync(session)).ShouldBe(["new_collection:Adhoc"]);
        (await RowsAsync(session, "SELECT * FROM new_collection")).ShouldBeEmpty();
        await transaction.RollbackAsync(CancellationToken.None);
        (await CollectionsAsync(session)).ShouldBeEmpty();
        await Should.ThrowAsync<DatabaseException>(async () => await session.GetCollectionAsync("new_collection", CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Collection DDL: DROP rolls back documents and indexes, then removes them together on commit")]
    public async Task Execute_DropInTransaction_ShouldRestoreDocumentsAndIndexOnRollbackAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        var saved = await collection.PutAsync(session, "one", "{\"score\":7}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: CancellationToken.None);

        // Act
        await using (var transaction = await session.BeginTransactionAsync(CancellationToken.None))
        {
            Success(await session.ExecuteAsync("DROP COLLECTION items", cancellationToken: CancellationToken.None));
            (await RowsAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
            (await CollectionsAsync(session)).ShouldBeEmpty();
            await transaction.RollbackAsync(CancellationToken.None);
        }

        // Assert
        (await collection.GetAsync(session, "one", CancellationToken.None)).ShouldNotBeNull().Version.ShouldBe(saved.Version);
        (await RowsAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES")).ShouldHaveSingleItem()[0].ShouldBe("by_score");
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            var query = (OqlSelectExpression)((OqlQueryStatement)new OqlQueryParser().Parse("SELECT score FROM items WHERE score = 7")).OqlExpression;
            new DocumentPlanner(database.Catalog, context.Snapshot, null).Plan(query).Access.ShouldBeOfType<DocumentIndexPath>();
        }
        finally { await database.Coordinator.RollbackAsync(context, CancellationToken.None); }
        (await RowsAsync(session, "SELECT score FROM items WHERE score = 7")).ShouldHaveSingleItem()[0].ShouldBe(7m);
        Success(await session.ExecuteAsync("DROP COLLECTION items", cancellationToken: CancellationToken.None));
        (await RowsAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
        await Should.ThrowAsync<DatabaseException>(async () => await session.GetCollectionAsync("items", CancellationToken.None));
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Collection DDL: snapshot conflicts take precedence over conditional no-ops")]
    [InlineData("CREATE COLLECTION x")]
    [InlineData("CREATE COLLECTION IF NOT EXISTS x")]
    [InlineData("DROP COLLECTION IF EXISTS x")]
    public async Task Execute_CollectionCommittedAfterSnapshot_ShouldAbortAsync(string statement)
    {
        // Arrange: two sessions, ordered commits; no scheduler race is required.
        await using var engine = DocumentDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var old = await database.CreateSessionAsync(CancellationToken.None);
        await using var current = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await old.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await current.ExecuteAsync("CREATE COLLECTION x", cancellationToken: CancellationToken.None);

        // Act
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
            await old.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        // Assert
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync(CancellationToken.None);
        (await CollectionsAsync(old)).ShouldBe(["x:Adhoc"]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Collection DDL: failed statements abort their explicit transaction")]
    [InlineData("CREATE COLLECTION items")]
    [InlineData("DROP COLLECTION absent")]
    [InlineData("CREATE COLLECTION COHESION_SCHEMA.INDEXES")]
    public async Task Execute_FailedDdl_ShouldAbortWholeTransactionAsync(string statement)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ddl", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION scratch", cancellationToken: CancellationToken.None);

        // Act
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        // Assert
        transaction.State.ShouldBe(TransactionState.Faulted);
        (await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("CREATE COLLECTION later", cancellationToken: CancellationToken.None)))
            .Message.ShouldContain("COHDBD001", Case.Sensitive);
        await transaction.RollbackAsync(CancellationToken.None);
        (await CollectionsAsync(session)).ShouldBe(["items:Adhoc"]);
    }

    private static void Success(DatabaseResult result)
    {
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        result.AffectedCount.ShouldBe(0);
    }

    private static async Task<string[]> CollectionsAsync(DocumentDatabaseSession session) =>
        (await RowsAsync(session, "SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY OBJECT_NAME"))
        .Select(row => $"{row[0]}:{row[1]}").ToArray();

    private static async Task<List<object?[]>> RowsAsync(DocumentDatabaseSession session, string statement)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
