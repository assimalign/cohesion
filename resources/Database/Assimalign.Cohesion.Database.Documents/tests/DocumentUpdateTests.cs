using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentUpdateTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: nested paths preserve order and MISSING follows the target shape")]
    [InlineData("SET extra.deep.value = 1", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{\"score\":1},\"tags\":[\"blue\"],\"extra\":{\"deep\":{\"value\":1}}}")]
    [InlineData("SET amount = 9, zeta = 1", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":9,\"details\":{\"score\":1},\"tags\":[\"blue\"],\"zeta\":1}")]
    [InlineData("SET tags[0] = 'red'", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{\"score\":1},\"tags\":[\"red\"]}")]
    [InlineData("SET tags[0] = nosuch", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{\"score\":1},\"tags\":[null]}")]
    [InlineData("SET extra = nosuch", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{\"score\":1},\"tags\":[\"blue\"]}")]
    [InlineData("SET extra.deep.value = MISSING", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{\"score\":1},\"tags\":[\"blue\"]}")]
    [InlineData("UNSET absent.deep", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{\"score\":1},\"tags\":[\"blue\"]}")]
    [InlineData("UNSET absent", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{\"score\":1},\"tags\":[\"blue\"]}")]
    [InlineData("SET amount = NULL UNSET tags", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":null,\"details\":{\"score\":1}}")]
    [InlineData("SET details = {'a':MISSING,'b':[1,MISSING,NULL]}", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{\"b\":[1,null,null]},\"tags\":[\"blue\"]}")]
    [InlineData("SET d['odd.name'] = 1, d.set = 2", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{\"score\":1},\"tags\":[\"blue\"],\"odd.name\":1,\"set\":2}")]
    [InlineData("SET extra.b = 2, extra.a = 1 UNSET details.score", "{\"name\":\"alpha\",\"category\":\"a\",\"amount\":2,\"details\":{},\"tags\":[\"blue\"],\"extra\":{\"b\":2,\"a\":1}}")]
    public async Task Execute_PathChanges_ShouldStoreExactOrderedJsonAsync(string changes, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = (await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull();
        var unmatched = (await items.GetAsync(session, "b", CancellationToken.None)).ShouldNotBeNull();
        // Act
        var result = await session.ExecuteAsync("UPDATE items AS d " + changes + " WHERE name = 'alpha'", cancellationToken: CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(1);
        var after = (await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull();
        after.Version.Value.ShouldBeGreaterThan(before.Version.Value);
        Encoding.UTF8.GetString(after.Content.Span).ShouldBe(expected);
        DocumentStorage.ValidateContent(after.Content);
        (await items.GetAsync(session, "b", CancellationToken.None)).ShouldNotBeNull().Version.ShouldBe(unmatched.Version);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: invalid or overlapping paths change no document")]
    [InlineData("SET name.first = 'x'", "COHDOC016")]
    [InlineData("UNSET name.first", "COHDOC016")]
    [InlineData("SET tags[5] = 'x'", "COHDOC016")]
    [InlineData("SET absent[0] = 1", "COHDOC016")]
    [InlineData("UNSET absent[0].x", "COHDOC016")]
    [InlineData("SET absent[0].x = MISSING", "COHDOC016")]
    [InlineData("UNSET tags[5].x", "COHDOC016")]
    [InlineData("SET tags[5].x = MISSING", "COHDOC016")]
    [InlineData("SET amount = 99 UNSET absent[0].x", "COHDOC016")]
    [InlineData("SET amount = 99, absent[0].x = MISSING", "COHDOC016")]
    [InlineData("SET amount = 99 UNSET tags[5].x", "COHDOC016")]
    [InlineData("SET amount = 99, tags[5].x = MISSING", "COHDOC016")]
    [InlineData("UNSET absent.deep[0].x", "COHDOC016")]
    [InlineData("SET details[0] = 1", "COHDOC016")]
    [InlineData("SET tags.name = 1", "COHDOC016")]
    [InlineData("SET d = {}", "COHDOC016")]
    [InlineData("UNSET tags[0]", "COHDOC016")]
    [InlineData("SET a = 1, a.b = 2", "COHDOC017")]
    [InlineData("SET a.b = 1, a = 2", "COHDOC017")]
    [InlineData("SET a = 1 UNSET a", "COHDOC017")]
    [InlineData("SET d.amount = 1, amount = 2", "COHDOC017")]
    [InlineData("SET details.score = 1, d['details']['score'] = 2", "COHDOC017")]
    [InlineData("UNSET a, a.b", "COHDOC017")]
    public async Task Execute_InvalidPath_ShouldPreserveAllContentAndVersionsAsync(string changes, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("UPDATE items d " + changes, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        if (changes.Contains("first", StringComparison.Ordinal)) { error.Message.ShouldContain("first", Case.Sensitive); }
        (await SnapshotAsync(items, session)).ShouldBe(before);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPDATE: every RHS reads the pre-update document and absent values remove properties")]
    public async Task Execute_SwapAndMissing_ShouldReadOriginalValuesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        // Act
        (await session.ExecuteAsync("UPDATE items SET amount = details.score, details.score = amount WHERE name = 'beta'", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        (await session.ExecuteAsync("UPDATE items SET amount = details.score WHERE name = 'delta'", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        // Assert
        using var beta = JsonDocument.Parse((await items.GetAsync(session, "b", CancellationToken.None)).ShouldNotBeNull().Content);
        beta.RootElement.GetProperty("amount").GetDecimal().ShouldBe(3m);
        beta.RootElement.GetProperty("details").GetProperty("score").GetDecimal().ShouldBe(6m);
        using var delta = JsonDocument.Parse((await items.GetAsync(session, "d", CancellationToken.None)).ShouldNotBeNull().Content);
        delta.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["name", "category", "tags"]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: seeks freeze candidates before moving indexed values")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_IndexedIncrement_ShouldUpdateEachSnapshotCandidateOnceAsync(bool indexed)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        if (indexed) { await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None); }
        const string statement = "UPDATE items SET amount = amount + 10 WHERE amount >= 4";
        var plan = (await DocumentPlanTestHelper.PlanAsync(database, statement)).ShouldBeOfType<DocumentUpdatePlan>();
        if (indexed) { plan.Access.ShouldBeOfType<DocumentIndexPath>(); }
        else { plan.Access.ShouldBeOfType<DocumentScanPath>(); }
        // Act
        (await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
        // Assert
        (await ValuesAsync(session, "SELECT amount FROM items WHERE amount >= 4 ORDER BY name")).ShouldBe([16m, 14m]);
        if (indexed)
        {
            var seek = await ValuesAsync(session, "SELECT name FROM items WHERE amount >= 4 ORDER BY name");
            await session.ExecuteAsync("DROP INDEX by_amount ON items", cancellationToken: CancellationToken.None);
            (await ValuesAsync(session, "SELECT name FROM items WHERE amount >= 4 ORDER BY name")).ShouldBe(seek);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: explicit transactions expose own writes and preserve other snapshots")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ExplicitTransaction_ShouldRespectVisibilityAndEndAsync(bool commit)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None);
        await using var observer = await database.CreateSessionAsync(CancellationToken.None);
        await using var snapshot = await observer.BeginTransactionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        // Act / Assert
        await session.ExecuteAsync("UPDATE items SET amount = amount + 10 WHERE amount >= 4", cancellationToken: CancellationToken.None);
        (await ValuesAsync(session, "SELECT amount FROM items WHERE amount >= 4 ORDER BY name")).ShouldBe([16m, 14m]);
        (await ValuesAsync(observer, "SELECT amount FROM items WHERE amount >= 4 ORDER BY name")).ShouldBe([6m, 4m]);
        if (commit) { await transaction.CommitAsync(CancellationToken.None); }
        else { await transaction.RollbackAsync(CancellationToken.None); }
        (await ValuesAsync(observer, "SELECT amount FROM items WHERE amount >= 4 ORDER BY name")).ShouldBe([6m, 4m]);
        await snapshot.RollbackAsync(CancellationToken.None);
        (await ValuesAsync(observer, "SELECT amount FROM items WHERE amount >= 4 ORDER BY name")).ShouldBe(commit ? [16m, 14m] : [6m, 4m]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: failure after an earlier write rolls back the entire operation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_LaterIndexFailure_ShouldUndoStatementAndPriorTransactionWorkAsync(bool explicitTransaction)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None);
        var before = await SnapshotAsync(items, session);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        if (transaction is not null) { await items.PutAsync(session, "prior", "{}"u8.ToArray(), cancellationToken: CancellationToken.None); }
        // Act: a writes successfully; b exceeds the B-tree key bound.
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "UPDATE items SET amount = CASE WHEN name = 'beta' THEN $large ELSE 20 END",
            new Dictionary<string, object?> { ["large"] = new string('x', 2000) }, CancellationToken.None));
        // Assert
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            var refusal = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM items", cancellationToken: CancellationToken.None));
            refusal.Message.ShouldContain("COHDBD001", Case.Sensitive);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await SnapshotAsync(items, session)).ShouldBe(before);
        (await items.GetAsync(session, "prior", CancellationToken.None)).ShouldBeNull();
        (await ValuesAsync(session, "SELECT amount FROM items WHERE amount >= 2 ORDER BY name")).ShouldBe([2m, 6m, 4m]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: a committed concurrent writer aborts every earlier update")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ConcurrentWriter_ShouldAbortWithoutPartialChangesAsync(bool explicitTransaction)
    {
        // Arrange: the statement waits for b's writer after it has frozen its candidates.
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await using var blocker = await database.CreateSessionAsync(CancellationToken.None);
        var other = await blocker.GetCollectionAsync("items", CancellationToken.None);
        await using var blocking = await blocker.BeginTransactionAsync(CancellationToken.None);
        await other.PutAsync(blocker, "b", "{\"name\":\"beta\",\"amount\":8}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = session.ExecuteAsync("UPDATE items SET amount = amount + 10 WHERE amount >= 2", cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        // Act
        await blocking.CommitAsync(timeout.Token);
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
        // Assert
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            await transaction.RollbackAsync(timeout.Token);
        }
        (await ValuesAsync(session, "SELECT amount FROM items WHERE amount >= 2 ORDER BY name")).ShouldBe([2m, 8m, 4m]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: invalid expressions fail over empty collections")]
    [InlineData("UPDATE items SET n = $absent WHERE 0", "COHDOC005")]
    [InlineData("UPDATE items SET n = COUNT(*)", "COHDOC001")]
    [InlineData("UPDATE items d SET n = META(other).id", "COHDOC001")]
    [InlineData("UPDATE items SET n = 1 WHERE COUNT(*) > 0", "COHDOC001")]
    [InlineData("UPDATE x SET n = 1", "COHDOC002")]
    public async Task Execute_InvalidExpression_ShouldRejectBeforeReadingAsync(string statement, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        if (code == "COHDOC002") { error.Message.ShouldBe("Collection 'x' does not exist."); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPDATE: schema ownership permits mutation and META version rejects stale preconditions")]
    public async Task Execute_SchemaOwnedAndVersionFilter_ShouldRespectMetadataAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var metadata = database.Catalog.FindCollection("items", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(metadata with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context, CancellationToken.None);
        await database.Coordinator.CommitAsync(context, CancellationToken.None);
        var original = (await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull();
        var parameters = new Dictionary<string, object?> { ["version"] = original.Version.Value };
        const string statement = "UPDATE items d SET amount = 0 WHERE META(d).version = $version";
        // Act / Assert
        (await session.ExecuteAsync(statement, parameters, CancellationToken.None)).AffectedCount.ShouldBe(1);
        var current = (await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull();
        current.Version.Value.ShouldBeGreaterThan(original.Version.Value);
        (await session.ExecuteAsync(statement, parameters, CancellationToken.None)).AffectedCount.ShouldBe(0);
        (await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull().Version.ShouldBe(current.Version);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: stored values enforce 128 levels with UPDATE diagnostics")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ExcessiveStoredDepth_ShouldPreserveContentAsync(bool constructor)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        string nested = new string('[', 128) + "1" + new string(']', 128);
        using var parameter = JsonDocument.Parse(nested, new JsonDocumentOptions { MaxDepth = 256 });
        string statement = constructor ? "UPDATE items SET extra = {'nested': $value}" : "UPDATE items SET extra = $value";
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement,
            new Dictionary<string, object?> { ["value"] = parameter.RootElement }, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC018");
        error.Message.ShouldBe("UPDATE JSON must be valid and nested no deeper than 128 levels.");
        (await SnapshotAsync(items, session)).ShouldBe(before);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: later path and RHS failures abort prior transaction work")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_LaterPatchFailure_ShouldPreserveEveryDocumentAsync(bool explicitTransaction, bool arithmetic)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        if (transaction is not null) { await items.PutAsync(session, "prior", "{}"u8.ToArray(), cancellationToken: CancellationToken.None); }
        string change = arithmetic ? "SET amount = CASE WHEN name = 'beta' THEN 1 / 0 ELSE 20 END" : "SET tags[0] = 'red'";
        // Act: tags is null on the final document; arithmetic fails on the second.
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("UPDATE items " + change, cancellationToken: CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(arithmetic ? "COHDOC006" : "COHDOC016");
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await SnapshotAsync(items, session)).ShouldBe(before);
        (await items.GetAsync(session, "prior", CancellationToken.None)).ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: cancellation while waiting for the writer changes nothing")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CancellationAtWriterLock_ShouldUndoOperationAsync(bool explicitTransaction)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        await using var blocker = await database.CreateSessionAsync(CancellationToken.None);
        var other = await blocker.GetCollectionAsync("items", CancellationToken.None);
        await using var blocking = await blocker.BeginTransactionAsync(CancellationToken.None);
        await other.PutAsync(blocker, "blocker", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = session.ExecuteAsync("UPDATE items SET amount = 0", cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        // Act
        timeout.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        // Assert
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        await blocking.RollbackAsync(CancellationToken.None);
        (await SnapshotAsync(items, session)).ShouldBe(before);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPDATE: array roots and nested array objects follow the same segment rules")]
    public async Task Execute_ArrayRoot_ShouldPatchElementsAndNestedPropertiesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        await items.PutAsync(session, "array", "[{\"a\":1,\"b\":2},[3,4]]"u8.ToArray(), cancellationToken: CancellationToken.None);
        // Act
        var result = await session.ExecuteAsync("UPDATE items d SET d[0].z.deep = d[0].a, d[1][0] = MISSING UNSET d[0].b", cancellationToken: CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(1);
        var document = (await items.GetAsync(session, "array", CancellationToken.None)).ShouldNotBeNull();
        Encoding.UTF8.GetString(document.Content.Span).ShouldBe("[{\"a\":1,\"z\":{\"deep\":1}},[null,4]]");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPDATE: the stored depth boundary includes the document root")]
    [InlineData(127, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task Execute_LongTarget_ShouldEnforceStorageDepthWithoutStackOverflowAsync(int segments, bool succeeds)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        var original = await items.PutAsync(session, "one", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);
        string statement = "UPDATE items SET " + string.Join('.', Enumerable.Repeat("a", segments)) + " = 1";
        // Act / Assert
        if (succeeds)
        {
            (await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
            DocumentStorage.ValidateContent((await items.GetAsync(session, "one", CancellationToken.None)).ShouldNotBeNull().Content);
        }
        else
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC018");
            (await items.GetAsync(session, "one", CancellationToken.None)).ShouldNotBeNull().Version.ShouldBe(original.Version);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPDATE: filter constructors keep the query depth allowance")]
    public async Task Execute_DeepFilterValue_ShouldApplyStorageDepthOnlyToWrittenContentAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        using var parameter = JsonDocument.Parse(new string('[', 128) + "1" + new string(']', 128), new JsonDocumentOptions { MaxDepth = 256 });
        // Act
        var result = await session.ExecuteAsync("UPDATE items SET amount = 0 WHERE {'wrapped': $value}",
            new Dictionary<string, object?> { ["value"] = parameter.RootElement }, CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(4);
        (await ValuesAsync(session, "SELECT amount FROM items")).ShouldBe([0m, 0m, 0m, 0m]);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPDATE: temporary RHS constructors retain the query depth allowance")]
    public async Task Execute_DeepTemporaryValue_ShouldStoreShallowResultAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        using var parameter = JsonDocument.Parse(new string('[', 128) + "1" + new string(']', 128), new JsonDocumentOptions { MaxDepth = 256 });
        // Act
        var result = await session.ExecuteAsync("UPDATE items SET amount = CASE WHEN {'deep': $value} THEN 1 ELSE 0 END",
            new Dictionary<string, object?> { ["value"] = parameter.RootElement }, CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(4);
        (await ValuesAsync(session, "SELECT amount FROM items")).ShouldBe([1m, 1m, 1m, 1m]);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPDATE: untouched UTF-8 values and property spellings survive byte for byte")]
    public async Task Execute_UnrelatedChange_ShouldPreserveRawPropertiesAndValuesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        const string original = """{"é東京<😀":"é東京<😀","escaped\u00e9":"\u00e9\/\"\n","nested\u003c":{"é東京<😀":[ "é東京<😀", 1.00e0 ],"n":1},"array":[ "é東京<😀", { "é東京<😀" : "\u6771京<😀" }, 1 ]} """;
        await items.PutAsync(session, "one", Encoding.UTF8.GetBytes(original), cancellationToken: CancellationToken.None);
        // Act
        await session.ExecuteAsync("UPDATE items d SET d['nested<'].n = 2, d.array[2] = 2, n = 2", cancellationToken: CancellationToken.None);
        // Assert
        string expected = original.TrimEnd().Replace("\"n\":1", "\"n\":2", StringComparison.Ordinal)
            .Replace("[ \"é東京<😀\", { \"é東京<😀\" : \"\\u6771京<😀\" }, 1 ]", "[\"é東京<😀\",{ \"é東京<😀\" : \"\\u6771京<😀\" },2]", StringComparison.Ordinal);
        expected = expected[..^1] + ",\"n\":2}";
        (await items.GetAsync(session, "one", CancellationToken.None)).ShouldNotBeNull().Content.ToArray().ShouldBe(Encoding.UTF8.GetBytes(expected));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPDATE: new UTF-8 values retain text with RFC 8259 escaping")]
    public async Task Execute_NewUnicodeValue_ShouldEscapeOnlyRequiredCharactersAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        await items.PutAsync(session, "one", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);
        // Act
        await session.ExecuteAsync("UPDATE items d SET d['é東京<😀'] = $text", new Dictionary<string, object?>
        {
            ["text"] = "é東京<😀\n\"\\\u0001",
        }, CancellationToken.None);
        // Assert
        var content = (await items.GetAsync(session, "one", CancellationToken.None)).ShouldNotBeNull().Content;
        content.ToArray().ShouldBe(Encoding.UTF8.GetBytes("{\"é東京<😀\":\"é東京<😀\\n\\\"\\\\\\u0001\"}"));
        DocumentStorage.ValidateContent(content);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPDATE: a later overflowing value identifies its own target")]
    public async Task Execute_LaterTargetDepthFailure_ShouldReportFailingTargetAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        using var parameter = JsonDocument.Parse(new string('[', 127) + "1" + new string(']', 127), new JsonDocumentOptions { MaxDepth = 256 });
        const string statement = "UPDATE items SET a = 1, b.c = $deep";
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement,
            new Dictionary<string, object?> { ["deep"] = parameter.RootElement }, CancellationToken.None));
        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC018");
        diagnostic.Start.ShouldBe(statement.IndexOf("b.c", StringComparison.Ordinal));
        diagnostic.End.ShouldBe(statement.IndexOf("b.c", StringComparison.Ordinal) + 3);
        (await SnapshotAsync(items, session)).ShouldBe(before);
    }

    private static async Task<DocumentCollection> SeedAsync(DocumentDatabaseSession session)
    {
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        (string Id, string Content)[] documents =
        [
            ("a", """{"name":"alpha","category":"a","amount":2,"details":{"score":1},"tags":["blue"]}"""),
            ("b", """{"name":"beta","category":"a","amount":6,"details":{"score":3},"tags":["green"]}"""),
            ("c", """{"name":"gamma","category":"b","amount":4,"details":{"score":2},"tags":["blue"]}"""),
            ("d", """{"name":"delta","category":"a","amount":null,"tags":null}"""),
        ];
        foreach (var (id, content) in documents)
        { await collection.PutAsync(session, id, Encoding.UTF8.GetBytes(content), cancellationToken: CancellationToken.None); }
        return collection;
    }

    private static async Task<string[]> SnapshotAsync(DocumentCollection items, DocumentDatabaseSession session)
    {
        var snapshot = new List<string>();
        foreach (string id in new[] { "a", "b", "c", "d" })
        {
            var document = (await items.GetAsync(session, id, CancellationToken.None)).ShouldNotBeNull();
            snapshot.Add(document.Version.Value + ":" + Encoding.UTF8.GetString(document.Content.Span));
        }
        return snapshot.ToArray();
    }

    private static async Task<object?[]> ValuesAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        return values.ToArray();
    }
}
