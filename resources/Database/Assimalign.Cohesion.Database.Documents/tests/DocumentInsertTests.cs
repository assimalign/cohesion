using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentInsertTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Object constructors: SELECT and INSERT resolve repeated names through one keyed object")]
    [InlineData("{'a': 1, 'a': 2}", "{\"a\":2}")]
    [InlineData("{'a': 1, 'a': NULL, 'A': 3}", "{\"a\":null,\"A\":3}")]
    [InlineData("{'o': {'a': 1, 'a': 2}}", "{\"o\":{\"a\":2}}")]
    [InlineData("{'a': MISSING, 'a': 2}", "{\"a\":2}")]
    [InlineData("{'a': 1, 'a': MISSING}", "{\"a\":1}")]
    public async Task Execute_RepeatedObjectMember_ShouldKeepLastIncludedValueAsync(string expression, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("object", new());
        var database = await engine.CreateDatabaseAsync("object", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        await items.PutAsync(session, "seed", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);

        // Act
        var selected = (await RowsAsync(session, $"SELECT {expression} AS o FROM items")).ShouldHaveSingleItem()[0].ShouldBeOfType<JsonElement>();
        var inserted = await session.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('object', {expression})", cancellationToken: CancellationToken.None);

        // Assert
        selected.GetRawText().ShouldBe(expected);
        inserted.AffectedCount.ShouldBe(1);
        var stored = (await items.GetAsync(session, "object", CancellationToken.None)).ShouldNotBeNull();
        Encoding.UTF8.GetString(stored.Content.Span).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - INSERT: invalid parameter JSON returns a coded failure before any mutation")]
    public async Task Execute_DuplicateParameterProperties_ShouldReturnCodedFailureAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("object", new());
        var database = await engine.CreateDatabaseAsync("object", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        using var json = JsonDocument.Parse("{\"a\":1,\"a\":2}");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT INTO items (KEY, VALUE) VALUES ('first', 1), ('object', $doc)",
            new Dictionary<string, object?> { ["doc"] = json.RootElement }, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC008");
        failure.InnerException.ShouldBeOfType<JsonException>();
        (await RowsAsync(session, "SELECT * FROM items")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - INSERT SELECT: keys bind output names and values preserve source JSON")]
    public async Task Execute_InsertSelect_ShouldCopySnapshotAndOmitMissingMembersAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("insert", new());
        var database = await engine.CreateDatabaseAsync("insert", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var archive = await session.CreateCollectionAsync("archive", CancellationToken.None);
        // Act
        (await session.ExecuteAsync("INSERT INTO archive (KEY k, VALUE v) SELECT META(d).id AS k, d AS v FROM items d WHERE d.category = 'a'", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(3);
        // Assert
        (await RowsAsync(session, "SELECT META().id FROM archive")).Select(row => row[0]).ShouldBe(["a", "b", "d"]);
        foreach (string id in new[] { "a", "b", "d" })
        {
            var source = (await items.GetAsync(session, id, CancellationToken.None)).ShouldNotBeNull();
            var copy = (await archive.GetAsync(session, id, CancellationToken.None)).ShouldNotBeNull();
            EqualJson(source.Content, copy.Content);
        }
        await session.ExecuteAsync("DROP COLLECTION archive", cancellationToken: CancellationToken.None);
        archive = await session.CreateCollectionAsync("archive", CancellationToken.None);
        (await session.ExecuteAsync("INSERT INTO archive (KEY k) SELECT META(d).id AS k, d.details.score AS score, d.amount AS amount FROM items d WHERE d.category = 'a'", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(3);
        EqualJson("""{"k":"d","amount":null}"""u8.ToArray(), (await archive.GetAsync(session, "d", CancellationToken.None)).ShouldNotBeNull().Content);
        EqualJson("""{"k":"a","score":1,"amount":2}"""u8.ToArray(), (await archive.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull().Content);
        // Projection order is the storage member order.
        Encoding.UTF8.GetString((await archive.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull().Content.Span)
            .ShouldBe("""{"k":"a","score":1,"amount":2}""");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - INSERT SELECT: a MISSING VALUE and a self-copy each roll back completely")]
    public async Task Execute_InvalidInsertSelect_ShouldLeaveBothCollectionsUnchangedAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("insert", new());
        var database = await engine.CreateDatabaseAsync("insert", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        await session.CreateCollectionAsync("archive", CancellationToken.None);
        var before = await RowsAsync(session, "SELECT META().id, META().version FROM items");
        // Act / Assert
        var missing = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT INTO archive (KEY k, VALUE v) SELECT META(d).id AS k, d.details AS v FROM items d", cancellationToken: CancellationToken.None));
        missing.Message.ShouldBe("INSERT VALUE must be a JSON value and cannot be MISSING.");
        (await RowsAsync(session, "SELECT * FROM archive")).ShouldBeEmpty();
        var duplicate = await Should.ThrowAsync<DocumentDuplicateKeyException>(async () => await session.ExecuteAsync(
            "INSERT INTO items (KEY k) SELECT META(d).id AS k FROM items d", cancellationToken: CancellationToken.None));
        duplicate.CollectionName.ShouldBe("items");
        duplicate.DocumentId.ShouldBe(new DocumentId("a"));
        duplicate.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC012");
        (await RowsAsync(session, "SELECT META().id, META().version FROM items")).SelectMany(row => row).ShouldBe(before.SelectMany(row => row));
        (await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - INSERT: any third-row failure preserves documents, versions and index seeks")]
    [InlineData("'a'", "$doc", true)]
    [InlineData("'e'", "$doc", true)]
    [InlineData("NULL", "$doc", false)]
    [InlineData("12", "$doc", false)]
    [InlineData("'  '", "$doc", false)]
    [InlineData("MISSING", "$doc", false)]
    [InlineData("'g'", "MISSING", false)]
    [InlineData("'g'", "name", false)]
    [InlineData("'g'", "$deep", false)]
    public async Task Execute_ThirdRowFailure_ShouldRollBackStatementAsync(string key, string value, bool duplicate)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("insert", new());
        var database = await engine.CreateDatabaseAsync("insert", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None);
        var before = await RowsAsync(session, "SELECT META().id, META().version, name FROM items");
        using var document = JsonDocument.Parse("""{"name":"new","amount":9}""");
        using var deep = JsonDocument.Parse(new string('[', 129) + "0" + new string(']', 129), new JsonDocumentOptions { MaxDepth = 256 });
        var parameters = new Dictionary<string, object?> { ["doc"] = document.RootElement, ["deep"] = deep.RootElement };
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"INSERT INTO items (KEY, VALUE) VALUES ('e', $doc), ('f', $doc), ({key}, {value})", parameters, CancellationToken.None));
        // Assert
        if (duplicate)
        {
            var error = failure.ShouldBeOfType<DocumentDuplicateKeyException>();
            error.DocumentId.Value.ShouldBe(key.Trim('\''));
            error.Message.ShouldContain(error.DocumentId.Value, Case.Sensitive);
        }
        (await RowsAsync(session, "SELECT META().id, META().version, name FROM items"))
            .SelectMany(row => row).ShouldBe(before.SelectMany(row => row));
        (await RowsAsync(session, "SELECT META().id FROM items WHERE amount >= 0"))
            .Select(row => row[0]).ShouldBe(["a", "b", "c"]);
        (await RowsAsync(session, "SELECT META().id FROM items")).Count.ShouldBe(4);
        (await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('e', $doc)", parameters, CancellationToken.None)).AffectedCount.ShouldBe(1);
        (await RowsAsync(session, "SELECT META().id FROM items WHERE amount = 9")).ShouldHaveSingleItem()[0].ShouldBe("e");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - INSERT: Schema ownership permits content writes and empty SELECT affects zero")]
    public async Task Execute_SchemaOwnedCollection_ShouldAcceptInsertAndEmptySourceAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("insert", new());
        var database = await engine.CreateDatabaseAsync("insert", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var metadata = database.Catalog.FindCollection("items", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(metadata with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context, CancellationToken.None);
        await database.Coordinator.CommitAsync(context, CancellationToken.None);
        // Act / Assert
        (await session.ExecuteAsync("INSERT INTO items (KEY k) SELECT META().id AS k FROM items", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(0);
        (await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('schema', NULL)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        (await RowsAsync(session, "SELECT META().id FROM items")).ShouldHaveSingleItem()[0].ShouldBe("schema");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - INSERT: failure aborts the whole explicit transaction and rollback restores reuse")]
    public async Task Execute_DuplicateInTransaction_ShouldAbortAllWorkAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("insert", new());
        var database = await engine.CreateDatabaseAsync("insert", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('prior', 1)", cancellationToken: CancellationToken.None);
        // Act
        await Should.ThrowAsync<DocumentDuplicateKeyException>(async () => await session.ExecuteAsync(
            "INSERT INTO items (KEY, VALUE) VALUES ('e', 2), ('f', 3), ('a', 4)", cancellationToken: CancellationToken.None));
        // Assert
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(session, "SELECT META().id FROM items")).Select(row => row[0]).ShouldBe(["a", "b", "c", "d"]);
        (await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('next', 5)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - INSERT: a committed same-key insert beyond the snapshot is a conflict")]
    public async Task Execute_KeyCommittedAfterSnapshot_ShouldAbortInsteadOfDuplicateAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("insert", new());
        var database = await engine.CreateDatabaseAsync("insert", CancellationToken.None);
        await using var first = await database.CreateSessionAsync(CancellationToken.None);
        await first.CreateCollectionAsync("items", CancellationToken.None);
        await using var transaction = await first.BeginTransactionAsync(CancellationToken.None);
        await using var second = await database.CreateSessionAsync(CancellationToken.None);
        await second.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('same', 1)", cancellationToken: CancellationToken.None);
        // Act / Assert
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await first.ExecuteAsync(
            "INSERT INTO items (KEY, VALUE) VALUES ('same', 2)", cancellationToken: CancellationToken.None));
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(first, "SELECT * FROM items")).ShouldHaveSingleItem()[0].ShouldBe(1m);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - META: external identity and increasing versions survive missing object members")]
    public async Task Execute_Meta_ShouldMatchCrudAndRemainSeparateFromContentPathsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("insert", new());
        var database = await engine.CreateDatabaseAsync("insert", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        (await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('e', {'score': MISSING, 'amount': NULL}), ('f', {'id':'content'})", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
        // Act
        var rows = await RowsAsync(session, "SELECT META(d).id AS id, META(d).version AS v FROM items d ORDER BY id");
        // Assert
        var objectGroups = await RowsAsync(session, "SELECT {'category': category} AS key, COUNT(*) AS n FROM items GROUP BY {'category': category} ORDER BY n DESC");
        objectGroups.Count.ShouldBe(3);
        objectGroups[0][0].ShouldBeOfType<JsonElement>().GetProperty("category").GetString().ShouldBe("a");
        objectGroups[0][1].ShouldBe(3m);
        var grouped = await RowsAsync(session, "SELECT META(d).id AS id, MAX(META(d).version) AS v FROM items d GROUP BY META(d).id ORDER BY id");
        grouped.SelectMany(row => row).ShouldBe(rows.SelectMany(row => row));
        ulong previous = 0;
        foreach (var row in rows)
        {
            var document = (await items.GetAsync(session, (string)row[0]!, CancellationToken.None)).ShouldNotBeNull();
            row[1].ShouldBe((decimal)document.Version.Value);
            document.Version.Value.ShouldBeGreaterThan(previous);
            previous = document.Version.Value;
        }
        EqualJson("""{"amount":null}"""u8.ToArray(), (await items.GetAsync(session, "e", CancellationToken.None)).ShouldNotBeNull().Content);
        await session.ExecuteAsync("CREATE INDEX by_id ON items (id)", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT d.name FROM items d WHERE META(d).id = 'b'")).ShouldHaveSingleItem()[0].ShouldBe("beta");
        await using var planningSession = await database.CreateSessionAsync(CancellationToken.None);
        var plan = await database.RunAsync(planningSession, operation => ValueTask.FromResult(new DocumentPlanner(database.Catalog,
            operation.Context.Snapshot, null).Plan((OqlSelectExpression)DocumentQueryRequest.FromOql("SELECT d.name FROM items d WHERE META(d).id = 'b'").Statement.OqlExpression)), CancellationToken.None);
        plan.Access.ShouldBeOfType<DocumentScanPath>();
        (await RowsAsync(session, "SELECT META().id FROM items WHERE id = 'content'")).ShouldHaveSingleItem()[0].ShouldBe("f");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - META: invalid metadata contexts fail during planning")]
    [InlineData("SELECT META(d).cas FROM items d", "META requires a stored document source, an optional matching source alias, and the member id or version.")]
    [InlineData("SELECT META(d) FROM items d", "META requires a stored document source, an optional matching source alias, and the member id or version.")]
    [InlineData("SELECT META(x).id FROM items d", "META requires a stored document source, an optional matching source alias, and the member id or version.")]
    [InlineData("SELECT META().id FROM COHESION_SCHEMA.INDEXES", "META requires a stored document source, an optional matching source alias, and the member id or version.")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('e', META().id)", "INSERT VALUES expressions cannot read document paths, metadata, or aggregates.")]
    [InlineData("INSERT INTO absent (KEY, VALUE) VALUES ('e', 1)", "Collection 'absent' does not exist.")]
    public async Task Execute_InvalidContext_ShouldGiveStablePlanningMessageAsync(string query, string message)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("insert", new());
        var database = await engine.CreateDatabaseAsync("insert", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        failure.Message.ShouldBe(message);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldNotBeNullOrEmpty();
    }

    private static async Task<DocumentCollection> SeedAsync(DocumentDatabaseSession session)
    {
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        (string Id, string Json)[] documents =
        [
            ("a", """{"name":"alpha","category":"a","amount":2,"details":{"score":1}}"""),
            ("b", """{"name":"beta","category":"a","amount":6,"details":{"score":3}}"""),
            ("c", """{"name":"gamma","category":"b","amount":4,"details":{"score":2}}"""),
            ("d", """{"name":"delta","category":"a","amount":null}"""),
        ];
        foreach (var (id, json) in documents)
        { await items.PutAsync(session, id, Encoding.UTF8.GetBytes(json), cancellationToken: CancellationToken.None); }
        return items;
    }

    private static async Task<List<object?[]>> RowsAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }

    private static void EqualJson(ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> actual)
    {
        using var left = JsonDocument.Parse(expected);
        using var right = JsonDocument.Parse(actual);
        JsonElement.DeepEquals(left.RootElement, right.RootElement).ShouldBeTrue();
    }
}
