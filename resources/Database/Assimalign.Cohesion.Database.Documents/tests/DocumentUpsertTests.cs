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
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentUpsertTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - UPSERT: existing documents are replaced whole and new identities are inserted")]
    public async Task Execute_MixedKeys_ShouldReplaceWholeDocumentAndAdvanceOnlyWrittenVersionsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        using var replacement = JsonDocument.Parse("""{"name":"alpha2","category":"a"}""");
        using var added = JsonDocument.Parse("""{"name":"zeta","category":"z"}""");
        // Act
        var result = await session.ExecuteAsync("UPSERT INTO items AS u (KEY, VALUE) VALUES ('a', $replacement), ('z', $new)",
            new Dictionary<string, object?> { ["replacement"] = replacement.RootElement, ["new"] = added.RootElement }, CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(2);
        result.ShouldNotBeAssignableTo<DatabaseResultSet>();
        var stored = (await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull();
        using var json = JsonDocument.Parse(stored.Content);
        JsonElement.DeepEquals(json.RootElement, replacement.RootElement).ShouldBeTrue();
        json.RootElement.TryGetProperty("amount", out _).ShouldBeFalse();
        json.RootElement.TryGetProperty("details", out _).ShouldBeFalse();
        json.RootElement.TryGetProperty("tags", out _).ShouldBeFalse();
        stored.Version.Value.ShouldBeGreaterThan(before["a"].Version.Value);
        foreach (string id in new[] { "b", "c", "d" })
        { (await items.GetAsync(session, id, CancellationToken.None)).ShouldNotBeNull().Version.ShouldBe(before[id].Version); }
        (await RowsAsync(session, "SELECT META(d).id AS id, d.name AS name, d.amount AS amount FROM items d ORDER BY id"))
            .ShouldBe(new object?[][] { ["a", "alpha2", null], ["b", "beta", 6m], ["c", "gamma", 4m], ["d", "delta", null], ["z", "zeta", null] });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPSERT: replaced index keys agree with scans")]
    public async Task Execute_IndexedReplacement_ShouldRemoveOldKeyAndAddNewKeyAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_score ON items (details.score)", cancellationToken: CancellationToken.None);
        // Act
        (await session.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('a', {'name':'alpha','details':{'score':7}})", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        // Assert
        foreach (int score in new[] { 1, 7 })
        {
            string query = $"SELECT name FROM items WHERE details.score = {score}";
            var plan = (await DocumentPlanTestHelper.PlanAsync(database, query)).ShouldBeOfType<DocumentPlan>();
            plan.Access.ShouldBeOfType<DocumentIndexPath>().Index.Name.ShouldBe("by_score");
            var indexed = await RowsAsync(session, query);
            await session.ExecuteAsync("DROP INDEX by_score ON items", cancellationToken: CancellationToken.None);
            ((await DocumentPlanTestHelper.PlanAsync(database, query)).ShouldBeOfType<DocumentPlan>().Access).ShouldBeOfType<DocumentScanPath>();
            (await RowsAsync(session, query)).ShouldBe(indexed);
            if (score == 1) { indexed.ShouldBeEmpty(); }
            else { indexed.ShouldHaveSingleItem()[0].ShouldBe("alpha"); }
            await session.ExecuteAsync("CREATE INDEX by_score ON items (details.score)", cancellationToken: CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - INSERT/UPSERT: repeated keys fail atomically in VALUES and SELECT")]
    [InlineData(false, "INSERT", "a")]
    [InlineData(true, "INSERT", "a")]
    [InlineData(false, "INSERT", "n")]
    [InlineData(true, "INSERT", "n")]
    [InlineData(false, "UPSERT", "a")]
    [InlineData(true, "UPSERT", "a")]
    [InlineData(false, "UPSERT", "n")]
    [InlineData(true, "UPSERT", "n")]
    public async Task Execute_RepeatedKey_ShouldRejectWholeStatementAsync(bool select, string verb, string key)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        string query = select ? $"{verb} INTO items (KEY k, VALUE v) SELECT '{key}' AS k, d AS v FROM items d" :
            $"{verb} INTO items (KEY, VALUE) VALUES ('{key}', {{'x':1}}), ('{key}', {{'x':2}})";
        // Act
        var failure = await Should.ThrowAsync<DocumentDuplicateKeyException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        failure.CollectionName.ShouldBe("items");
        failure.DocumentId.Value.ShouldBe(key);
        failure.Message.ShouldBe($"Document key '{key}' appears more than once in one statement.");
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC012");
        diagnostic.Message.ShouldBe(failure.Message);
        await AssertUnchangedAsync(items, session, before);
        (await items.GetAsync(session, "n", CancellationToken.None)).ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPSERT: invalid third rows preserve content, versions and indexes")]
    [InlineData("NULL", "1", "COHDOC008")]
    [InlineData("12", "1", "COHDOC008")]
    [InlineData("'  '", "1", "COHDOC008")]
    [InlineData("MISSING", "1", "COHDOC008")]
    [InlineData("'z'", "MISSING", "COHDOC008")]
    [InlineData("'z'", "$absent", "COHDOC005")]
    [InlineData("'z'", "1e-29", "COHDOC007")]
    public async Task Execute_InvalidThirdRow_ShouldLeaveAutocommitSessionReusableAsync(string key, string value, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_score ON items (details.score)", cancellationToken: CancellationToken.None);
        var before = await SnapshotAsync(items, session);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"UPSERT INTO items (KEY, VALUE) VALUES ('a', 10), ('e', 20), ({key}, {value})", cancellationToken: CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        await AssertUnchangedAsync(items, session, before);
        (await items.GetAsync(session, "e", CancellationToken.None)).ShouldBeNull();
        (await RowsAsync(session, "SELECT name FROM items WHERE details.score = 1")).ShouldHaveSingleItem()[0].ShouldBe("alpha");
        (await session.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('z', NULL)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPSERT: failure aborts an entire explicit transaction, including prior writes")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_FailureInTransaction_ShouldAbortUntilRollbackAsync(bool explicitTransaction)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_score ON items (details.score)", cancellationToken: CancellationToken.None);
        var before = await SnapshotAsync(items, session);
        await using var transaction = explicitTransaction ? await session.BeginTransactionAsync(CancellationToken.None) : null;
        if (explicitTransaction) { await session.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('prior', 1)", cancellationToken: CancellationToken.None); }
        // The third row is valid JSON, but its index entry fails after earlier rows were written.
        using var document = JsonDocument.Parse("{\"details\":{\"score\":\"" + new string('x', 2000) + "\"}}");
        // Act
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "UPSERT INTO items (KEY, VALUE) VALUES ('a', {'details':{'score':7}}), ('e', 2), ('z', $doc)",
            new Dictionary<string, object?> { ["doc"] = document.RootElement }, CancellationToken.None));
        // Assert
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            var refusal = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM items", cancellationToken: CancellationToken.None));
            refusal.Message.ShouldContain("COHDBD001", Case.Sensitive);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        await AssertUnchangedAsync(items, session, before);
        (await items.GetAsync(session, "e", CancellationToken.None)).ShouldBeNull();
        (await items.GetAsync(session, "prior", CancellationToken.None)).ShouldBeNull();
        (await RowsAsync(session, "SELECT name FROM items WHERE details.score = 1")).ShouldHaveSingleItem()[0].ShouldBe("alpha");
        (await RowsAsync(session, "SELECT name FROM items WHERE details.score = 7")).ShouldBeEmpty();
        (await session.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('next', 1)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPSERT SELECT: output bindings share INSERT MISSING and complete-row rules")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_Select_ShouldReplaceSnapshotRowsAndOmitMissingMembersAsync(bool explicitValue)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var archive = await session.CreateCollectionAsync("archive", CancellationToken.None);
        await archive.PutAsync(session, "a", "{\"old\":true}"u8.ToArray(), cancellationToken: CancellationToken.None);
        string query = explicitValue ? "UPSERT INTO archive (KEY k, VALUE v) SELECT META(d).id AS k, d AS v FROM items d" :
            "UPSERT INTO archive (KEY k) SELECT META(d).id AS k, d.details.score AS score, d.amount AS amount FROM items d";
        // Act
        (await session.ExecuteAsync(query, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(4);
        // Assert
        using var a = JsonDocument.Parse((await archive.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull().Content);
        a.RootElement.TryGetProperty("old", out _).ShouldBeFalse();
        if (explicitValue)
        {
            using var source = JsonDocument.Parse((await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull().Content);
            JsonElement.DeepEquals(a.RootElement, source.RootElement).ShouldBeTrue();
        }
        else
        {
            Encoding.UTF8.GetString((await archive.GetAsync(session, "d", CancellationToken.None)).ShouldNotBeNull().Content.Span).ShouldBe("{\"k\":\"d\",\"amount\":null}");
        }
        (await session.ExecuteAsync("UPSERT INTO archive (KEY k) SELECT META().id AS k FROM items WHERE FALSE", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(0);
        // Self-source is fully materialized before replacement removes the fields it reads.
        (await session.ExecuteAsync("UPSERT INTO items (KEY k) SELECT META().id AS k, details.score AS score FROM items", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(4);
        using var self = JsonDocument.Parse((await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull().Content);
        self.RootElement.GetProperty("score").GetInt32().ShouldBe(1);
        self.RootElement.TryGetProperty("details", out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPSERT SELECT: MISSING bindings reject the complete statement")]
    [InlineData("KEY k, VALUE v", "META().id AS k, details AS v")]
    [InlineData("KEY k", "absent AS k")]
    public async Task Execute_MissingSelectBinding_ShouldWriteNothingAsync(string header, string projection)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"UPSERT INTO items ({header}) SELECT {projection} FROM items", cancellationToken: CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC008");
        await AssertUnchangedAsync(items, session, before);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPSERT: content replacements keep committed and older snapshots consistent")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_ExplicitTransaction_ShouldPublishOnlyCommittedReplacementAsync(bool commit)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(writer);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        var observed = await reader.GetCollectionAsync("items", CancellationToken.None);
        await using var snapshot = await reader.BeginTransactionAsync(CancellationToken.None);
        await using var transaction = await writer.BeginTransactionAsync(CancellationToken.None);
        // Act
        (await writer.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('a', 42), ('new', 7)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
        Encoding.UTF8.GetString((await items.GetAsync(writer, "a", CancellationToken.None)).ShouldNotBeNull().Content.Span).ShouldBe("42");
        if (commit) { await transaction.CommitAsync(CancellationToken.None); }
        else { await transaction.RollbackAsync(CancellationToken.None); }
        // Assert
        using var old = JsonDocument.Parse((await observed.GetAsync(reader, "a", CancellationToken.None)).ShouldNotBeNull().Content);
        old.RootElement.GetProperty("name").GetString().ShouldBe("alpha");
        await snapshot.RollbackAsync(CancellationToken.None);
        ((await observed.GetAsync(reader, "new", CancellationToken.None)) is not null).ShouldBe(commit);
        using var latest = JsonDocument.Parse((await observed.GetAsync(reader, "a", CancellationToken.None)).ShouldNotBeNull().Content);
        latest.RootElement.ValueKind.ShouldBe(commit ? JsonValueKind.Number : JsonValueKind.Object);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPSERT: an intervening committed writer aborts a stale snapshot")]
    public async Task Execute_WriterCommittedAfterSnapshot_ShouldAbortAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var first = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(first);
        await using var transaction = await first.BeginTransactionAsync(CancellationToken.None);
        await using var second = await database.CreateSessionAsync(CancellationToken.None);
        await second.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('a', 9)", cancellationToken: CancellationToken.None);
        // Act
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await first.ExecuteAsync(
            "UPSERT INTO items (KEY, VALUE) VALUES ('e', 1), ('a', 2)", cancellationToken: CancellationToken.None));
        // Assert
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(first, "SELECT * FROM items WHERE META().id = 'a'")).ShouldHaveSingleItem()[0].ShouldBe(9m);
        (await RowsAsync(first, "SELECT * FROM items WHERE META().id = 'e'")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UPSERT: Schema ownership permits replacement and insertion")]
    public async Task Execute_SchemaOwnedCollection_ShouldPermitContentChangesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var metadata = database.Catalog.FindCollection("items", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(metadata with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context, CancellationToken.None);
        await database.Coordinator.CommitAsync(context, CancellationToken.None);
        // Act / Assert
        (await session.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('a', NULL), ('z', 1)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPSERT: target scope refusals preserve the session")]
    [InlineData("COHESION_SCHEMA.INDEXES", "System collection 'COHESION_SCHEMA.INDEXES' is read-only.")]
    [InlineData("COHESION_SCHEMA.OBJECT_OWNERSHIP", "System collection 'COHESION_SCHEMA.OBJECT_OWNERSHIP' is read-only.")]
    [InlineData("x", "Collection 'x' does not exist.")]
    public async Task Execute_InvalidTarget_ShouldReportExistingScopeErrorAsync(string collection, string message)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"UPSERT INTO {collection} (KEY, VALUE) VALUES ('a', 1)", cancellationToken: CancellationToken.None));
        // Assert
        failure.Message.ShouldBe(message);
        (await session.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('z', 1)", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPSERT: Unicode JSON uses INSERT serialization byte for byte")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_UnicodeValue_ShouldPreserveUtf8LikeInsertAsync(bool parameter)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("upsert", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        string text = "é東京<&😀\"\\" + new string(Enumerable.Range(0, 32).Select(value => (char)value).ToArray()) + "\u2028\u2029";
        const string escaped = "é東京<&😀\\\"\\\\" +
            "\\u0000\\u0001\\u0002\\u0003\\u0004\\u0005\\u0006\\u0007\\b\\t\\n\\u000b\\f\\r\\u000e\\u000f" +
            "\\u0010\\u0011\\u0012\\u0013\\u0014\\u0015\\u0016\\u0017\\u0018\\u0019\\u001a\\u001b\\u001c\\u001d\\u001e\\u001f\u2028\u2029";
        const string content = "{\"" + escaped + "\":\"" + escaped + "\"}";
        using var json = JsonDocument.Parse(content);
        var parameters = new Dictionary<string, object?> { ["doc"] = json.RootElement };
        string value = parameter ? "$doc" : "{'" + text + "':'" + text + "'}";
        // Act
        await session.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('inserted', {value})", parameters, CancellationToken.None);
        await session.ExecuteAsync($"UPSERT INTO items (KEY, VALUE) VALUES ('a', {value})", parameters, CancellationToken.None);
        // Assert
        var inserted = (await items.GetAsync(session, "inserted", CancellationToken.None)).ShouldNotBeNull();
        var replaced = (await items.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull();
        replaced.Content.ToArray().ShouldBe(inserted.Content.ToArray());
        replaced.Content.ToArray().ShouldBe(Encoding.UTF8.GetBytes(content));
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - INSERT/UPSERT: shared validation failures name the statement verb")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    public async Task Execute_InvalidWrite_ShouldNameItsVerbAsync(string verb)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("messages", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        using var limit = JsonDocument.Parse(new string('[', 128) + "0" + new string(']', 128), new JsonDocumentOptions { MaxDepth = 256 });
        using var deep = JsonDocument.Parse(new string('[', 129) + "0" + new string(']', 129), new JsonDocumentOptions { MaxDepth = 256 });
        var parameters = new Dictionary<string, object?> { ["limit"] = limit.RootElement, ["deep"] = deep.RootElement };
        const string context = "VALUES expressions cannot read document paths, metadata, or aggregates.";
        const string json = "JSON must be valid and nested no deeper than 128 levels.";
        (string Input, string Code, string Message)[] cases =
        [
            ("(KEY, VALUE) VALUES (MISSING, 1)", "COHDOC008", "KEY must evaluate to a nonblank string."),
            ("(KEY, VALUE) VALUES ('n', MISSING)", "COHDOC008", "VALUE must be a JSON value and cannot be MISSING."),
            ("(KEY, VALUE) VALUES ('n', d.name)", "COHDOC001", context),
            ("(KEY, VALUE) VALUES ('n', META().id)", "COHDOC001", context),
            ("(KEY, VALUE) VALUES ('n', COUNT(*))", "COHDOC001", context),
            ("(KEY absent) SELECT 'n' AS k FROM items", "COHDOC001", "binding 'absent' must name a SELECT output column."),
            ("(KEY k, VALUE absent) SELECT 'n' AS k FROM items", "COHDOC001", "binding 'absent' must name a SELECT output column."),
            ("(KEY, VALUE) VALUES ('n', $deep)", "COHDOC008", json),
            ("(KEY, VALUE) VALUES ('n', {'v':$limit})", "COHDOC008", json),
            ("(KEY, VALUE) VALUES ('n', [$limit])", "COHDOC008", json),
            ("(KEY k, VALUE v) SELECT 'n' AS k, {'v':$limit} AS v FROM items", "COHDOC008", json),
            ("(KEY k) SELECT 'n' AS k, $limit AS v FROM items", "COHDOC008", json),
        ];
        foreach (var test in cases)
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                $"{verb} INTO items {test.Input}", parameters, CancellationToken.None));
            // Assert
            failure.Message.ShouldBe(verb + " " + test.Message);
            var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe(test.Code);
            diagnostic.Message.ShouldBe(failure.Message);
            await AssertUnchangedAsync(items, session, before);
            (await items.GetAsync(session, "n", CancellationToken.None)).ShouldBeNull();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UPSERT: input validation rejects unused invalid inputs before EXPLAIN or an empty SELECT")]
    [InlineData(false, "$absent", "COHDOC005")]
    [InlineData(true, "$absent", "COHDOC005")]
    [InlineData(false, "$n", "COHDOC007")]
    [InlineData(true, "$n", "COHDOC007")]
    [InlineData(false, "1e-29", "COHDOC007")]
    [InlineData(true, "1e-29", "COHDOC007")]
    public async Task Execute_UnusedInvalidInput_ShouldRejectBeforeExecutionAsync(bool explain, string operand, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("inputs", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await SeedAsync(session);
        var before = await SnapshotAsync(items, session);
        using var lossy = JsonDocument.Parse("1e-29");
        var parameters = new Dictionary<string, object?> { ["n"] = lossy.RootElement };
        string query = explain ? $"EXPLAIN UPSERT INTO items (KEY, VALUE) VALUES ('n', {operand})" :
            $"UPSERT INTO items (KEY k, VALUE v) SELECT 'n' AS k, {operand} AS v FROM items WHERE FALSE";
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, parameters, CancellationToken.None));
        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(query.IndexOf(operand, StringComparison.Ordinal));
        diagnostic.End.ShouldBe(diagnostic.Start + operand.Length);
        diagnostic.Line.ShouldBe(1);
        await AssertUnchangedAsync(items, session, before);
        (await items.GetAsync(session, "n", CancellationToken.None)).ShouldBeNull();
    }

    private static async Task<DocumentCollection> SeedAsync(DocumentDatabaseSession session)
    {
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        (string Id, string Json)[] documents =
        [
            ("a", """{"name":"alpha","category":"a","amount":2,"details":{"score":1},"tags":["blue"]}"""),
            ("b", """{"name":"beta","category":"a","amount":6,"details":{"score":3}}"""),
            ("c", """{"name":"gamma","category":"b","amount":4,"details":{"score":2}}"""),
            ("d", """{"name":"delta","category":"a","amount":null}"""),
        ];
        foreach (var (id, json) in documents)
        { await items.PutAsync(session, id, Encoding.UTF8.GetBytes(json), cancellationToken: CancellationToken.None); }
        return items;
    }

    private static async Task<Dictionary<string, Document>> SnapshotAsync(DocumentCollection items, DocumentDatabaseSession session)
    {
        var snapshot = new Dictionary<string, Document>();
        foreach (string id in new[] { "a", "b", "c", "d" })
        { snapshot.Add(id, (await items.GetAsync(session, id, CancellationToken.None)).ShouldNotBeNull()); }
        return snapshot;
    }

    private static async Task AssertUnchangedAsync(DocumentCollection items, DocumentDatabaseSession session, Dictionary<string, Document> before)
    {
        foreach (var pair in before)
        {
            var actual = (await items.GetAsync(session, pair.Key, CancellationToken.None)).ShouldNotBeNull();
            actual.Version.ShouldBe(pair.Value.Version);
            actual.Content.ToArray().ShouldBe(pair.Value.Content.ToArray());
        }
    }

    private static async Task<List<object?[]>> RowsAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
