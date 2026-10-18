using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentQueryTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Composite seeks equal scans over randomized mixed shapes (#1135)")]
    [InlineData(1135, false)]
    [InlineData(2026, false)]
    [InlineData(7919, false)]
    [InlineData(1135, true)]
    [InlineData(2026, true)]
    [InlineData(7919, true)]
    public async Task Execute_RandomCompositePredicates_ShouldEqualScanAsync(int seed, bool unique)
    {
        // Arrange: omitted properties, nulls, ordinal strings, numeric scales and shapes.
        await using var engine = DocumentDatabaseEngine.Create("composite-parity", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var random = new Random(seed);
        string?[] values = [null, "null", "true", "false", "-1", "0", "1", "2", "2.0", "3", "''", "'x'", "'z'", "'😀'", "'\ue000'", "[2]", "{'v':2}"];
        for (int i = 0; i < 90; i++)
        {
            var members = new List<string>();
            foreach (string path in new[] { "a", "b", "c" })
            {
                string? value = unique && path == "c" ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) :
                    values[random.Next(unique ? values.Length - 2 : values.Length)];
                if (value is not null) { members.Add($"'{path}':{value}"); }
            }
            await session.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('{i:D3}', {{{string.Join(',', members)}}})", cancellationToken: CancellationToken.None);
        }
        var predicates = new List<(string Text, bool Seek)>
        {
            ("a = 1", true), ("a = 1 AND b > 2", true), ("a = @p AND b = 'x'", true),
            ("b = 2", false), ("a = 2.0 AND b <= 2 AND c > 1", true),
            ("a >= 1 AND a <= 'z'", true), ("a = 2 AND a = 3", true),
            ("a >= 3 AND a < 1", true), ("2 <= a AND b BETWEEN 1 AND 3", true),
            ("a BETWEEN 1 AND 3", true), ("a = 1 OR b = 2", false), ("a IN (1,2)", false),
            ("a IS MISSING", false), ("a IS NULL", false), ("a >= @q AND b <= 2", true),
            ("a = 2 AND b = 2 AND c <= 45", true), ("a = 2 AND b = 2 AND c > 45", true),
        };
        string[] operations = ["=", ">", ">=", "<", "<="];
        for (int i = 0; i < 28; i++)
        {
            string predicate = $"a {operations[random.Next(5)]} {random.Next(4)}";
            if (i % 2 == 0) { predicate += $" AND b {operations[random.Next(5)]} {random.Next(4)}"; }
            if (i % 3 == 0) { predicate += $" AND c {operations[random.Next(5)]} {random.Next(4)}"; }
            predicates.Add((predicate, true));
        }
        var parameters = new Dictionary<string, object?> { ["p"] = 1, ["q"] = 2L };
        var queries = predicates.Select(item => $"SELECT META().id AS id FROM items WHERE {item.Text}").ToArray();
        var baseline = new List<string?[]>();
        foreach (string query in queries) { baseline.Add((await RowsAsync(session, query, parameters)).Select(row => row.GetString(0)).ToArray()); }

        // Act / Assert: each key width must preserve every scan row, before and after DROP.
        foreach (string paths in (unique ? new[] { "a, b, c" } : new[] { "a", "a, b", "a, b, c" }))
        {
            await session.ExecuteAsync($"CREATE {(unique ? "UNIQUE " : "")}INDEX ix ON items ({paths})", cancellationToken: CancellationToken.None);
            for (int i = 0; i < queries.Length; i++)
            {
                (await RowsAsync(session, queries[i], parameters)).Select(row => row.GetString(0)).ShouldBe(baseline[i], $"seed={seed}; unique={unique}; paths={paths}; {queries[i]}");
                ((await PlanAsync(database, queries[i], parameters)).Access is DocumentIndexPath).ShouldBe(predicates[i].Seek);
            }
            await session.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: CancellationToken.None);
            for (int i = 0; i < queries.Length; i++)
            { (await RowsAsync(session, queries[i], parameters)).Select(row => row.GetString(0)).ShouldBe(baseline[i], $"seed={seed}; after drop; {queries[i]}"); }
            // Only the full unique key is guaranteed distinct in this corpus.
            if (unique) { break; }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Unique composite writes, nulls, missing keys and failed builds (#1135)")]
    public async Task Execute_UniqueCompositeIndex_ShouldEnforceImmediateConstraintsAsync()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("unique", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await session.ExecuteAsync("CREATE UNIQUE INDEX uq ON items (a, b)");
        await PutAsync(collection, session, "one", "{\"a\":1,\"b\":2.0}");

        // Act / Assert: equal normalized keys conflict; self-replacement succeeds.
        var error = await Should.ThrowAsync<DocumentConstraintViolationException>(async () =>
            await PutAsync(collection, session, "two", "{\"a\":1.00,\"b\":2}"));
        error.IndexName.ShouldBe("uq"); error.CollectionName.ShouldBe("items"); error.SqlState.ShouldBe("23505");
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC020");
        await PutAsync(collection, session, "one", "{\"a\":1,\"b\":2}");
        foreach (string json in new[] { "{}", "{\"a\":1}", "{\"a\":null,\"b\":2}", "{\"a\":1,\"b\":null}" })
        {
            await PutAsync(collection, session, Guid.NewGuid().ToString("N"), json);
            await PutAsync(collection, session, Guid.NewGuid().ToString("N"), json);
        }
        foreach (string json in new[] { "{\"a\":1,\"b\":[]}", "{\"a\":{},\"b\":2}" })
        {
            var shape = await Should.ThrowAsync<DocumentConstraintViolationException>(async () => await PutAsync(collection, session, "bad", json));
            shape.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC021");
            shape.Message.ShouldContain("uq"); shape.Path.ShouldNotBeNull();
        }
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await collection.DeleteAsync(session, "one", cancellationToken: CancellationToken.None);
            await PutAsync(collection, session, "replacement", "{\"a\":1,\"b\":2}");
            await transaction.CommitAsync(CancellationToken.None);
        }
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await Should.ThrowAsync<DocumentConstraintViolationException>(async () => await PutAsync(collection, session, "bad", "{\"a\":1,\"b\":2}"));
            var aborted = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM items"));
            aborted.Message.ShouldContain("COHDBD001");
            await transaction.RollbackAsync(CancellationToken.None);
        }
        await session.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS uq ON items (other)");
        await session.ExecuteAsync("DROP INDEX IF EXISTS absent ON items");
        await session.ExecuteAsync("DROP INDEX uq ON items");
        await PutAsync(collection, session, "duplicate", "{\"a\":1,\"b\":2}");
        await Should.ThrowAsync<DocumentConstraintViolationException>(async () => await session.ExecuteAsync("CREATE UNIQUE INDEX uq ON items (a,b)"));
        (await RowsAsync(session, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
        await session.ExecuteAsync("CREATE INDEX uq ON items (a,b)");
        await session.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS uq ON items (a)");
        (await RowsAsync(session, "SELECT IS_UNIQUE FROM COHESION_SCHEMA.INDEXES")).Single().GetBoolean(0).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Composite choice ranks equality, range, uniqueness and name (#1135)")]
    public async Task Plan_CompetingIndexes_ShouldChooseLongestEqualityPrefixAsync()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("choice", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        await session.CreateCollectionAsync("items");
        foreach (string ddl in new[] { "CREATE INDEX a_single ON items (a)", "CREATE INDEX b_pair ON items (a,b)",
            "CREATE INDEX a_triple ON items (a,b,c)", "CREATE UNIQUE INDEX z_unique ON items (a,b)" })
        { await session.ExecuteAsync(ddl); }

        // Act / Assert.
        ((await PlanAsync(database, "SELECT * FROM items WHERE a = 1 AND b = 2")).Access as DocumentIndexPath)!.Index.Name.ShouldBe("z_unique");
        ((await PlanAsync(database, "SELECT * FROM items WHERE a = 1 AND b = 2 AND c > 3")).Access as DocumentIndexPath)!.Index.Name.ShouldBe("a_triple");
        ((await PlanAsync(database, "SELECT * FROM items WHERE a = 1 AND b > 2")).Access as DocumentIndexPath)!.Index.Name.ShouldBe("z_unique");
        (await PlanAsync(database, "SELECT * FROM items WHERE b = 2")).Access.ShouldBeOfType<DocumentScanPath>();
        var detail = await RowsAsync(session, "EXPLAIN SELECT * FROM items WHERE a = 1 AND b = 2 AND c <= 3");
        detail.Single(row => row.GetString(2) == "IndexSeek").GetString(5).ShouldBe("a = 1 AND b = 2 AND c <= 3");
        var cap = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"CREATE INDEX too_wide ON items ({string.Join(',', Enumerable.Range(0,33).Select(i => "p" + i))})"));
        cap.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("OQL0009");
    }
    [Fact(DisplayName = "Cohesion Test [Documents] - UNIQUE DML failures roll back every index and document (#1135)")]
    public async Task Execute_MultirowUniqueViolation_ShouldRollbackWholeStatementAsync()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("unique-dml", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        await session.CreateCollectionAsync("items");
        await session.ExecuteAsync("CREATE UNIQUE INDEX uq ON items (a,b)");
        await session.ExecuteAsync("CREATE INDEX lookup ON items (b,a)");

        // Act / Assert: the first INSERT is also undone when the second violates uniqueness.
        await Should.ThrowAsync<DocumentConstraintViolationException>(async () => await session.ExecuteAsync(
            "INSERT INTO items (KEY, VALUE) VALUES ('a', {'a':1,'b':2}), ('b', {'a':1,'b':2})"));
        (await RowsAsync(session, "SELECT * FROM items WHERE a = 1 AND b = 2")).ShouldBeEmpty();
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {'a':1,'b':2}), ('b', {'a':2,'b':2})");
        await Should.ThrowAsync<DocumentConstraintViolationException>(async () => await session.ExecuteAsync("UPDATE items SET a = 3 - a"));
        (await RowsAsync(session, "SELECT a FROM items WHERE b = 2 ORDER BY a")).Select(row => (decimal)row.GetValue(0)!).ShouldBe([1m,2m]);
        await Should.ThrowAsync<DocumentConstraintViolationException>(async () => await session.ExecuteAsync(
            "UPSERT INTO items (KEY, VALUE) VALUES ('a', {'a':3,'b':2}), ('c', {'a':2,'b':2})"));
        (await RowsAsync(session, "SELECT a FROM items WHERE b = 2 ORDER BY a")).Select(row => (decimal)row.GetValue(0)!).ShouldBe([1m,2m]);
        (await RowsAsync(session, "SELECT * FROM items WHERE a = 3")).ShouldBeEmpty();
        await session.ExecuteAsync("UPDATE items SET a = a + 10 WHERE b = 2");
        await session.ExecuteAsync("DELETE FROM items WHERE a = 11 AND b = 2");
        (await RowsAsync(session, "SELECT a FROM items WHERE b = 2")).ShouldHaveSingleItem().GetValue(0).ShouldBe(12m);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Composite oversized keys fail before publication (#1135)")]
    public async Task Execute_CompositeKeyTooLarge_ShouldPreserveDefinitionsAndDocumentsAsync()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("wide", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        string large = "{\"a\":\"" + new string('x', 300) + "\",\"b\":\"" + new string('y', 300) + "\"}";
        await PutAsync(collection, session, "a", large);

        // Act / Assert.
        await Should.ThrowAsync<DocumentCatalogException>(async () => await session.ExecuteAsync("CREATE INDEX ix ON items (a,b)"));
        (await RowsAsync(session, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
        await collection.DeleteAsync(session, "a");
        await session.ExecuteAsync("CREATE INDEX ix ON items (a,b)");
        await PutAsync(collection, session, "a", "{\"a\":1,\"b\":2}");
        await Should.ThrowAsync<DocumentCatalogException>(async () => await PutAsync(collection, session, "a", large));
        (await RowsAsync(session, "SELECT a FROM items WHERE a = 1 AND b = 2")).ShouldHaveSingleItem().GetValue(0).ShouldBe(1m);
        foreach (string value in new[] { "[]", "{}" })
        {
            await session.ExecuteAsync("DROP INDEX ix ON items");
            await PutAsync(collection, session, "a", "{\"a\":1,\"b\":" + value + "}");
            var error = await Should.ThrowAsync<DocumentConstraintViolationException>(async () => await session.ExecuteAsync("CREATE UNIQUE INDEX ix ON items (a,b)"));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC021");
            (await RowsAsync(session, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
            await session.ExecuteAsync("CREATE INDEX ix ON items (a,b)");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Conditional DROP preserves schema ownership precedence (#1135)")]
    public async Task Execute_ConditionalIndexDdl_ShouldHonorOwnershipAndSnapshotConflictsAsync()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("index-ownership", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        await session.CreateCollectionAsync("items");
        await session.ExecuteAsync("CREATE INDEX ix ON items (a,b)");
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var collection = database.Catalog.FindCollection("items", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(collection with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context);
        await database.Coordinator.CommitAsync(context);

        // Act / Assert.
        await session.ExecuteAsync("DROP INDEX IF EXISTS absent ON items");
        var ownership = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await session.ExecuteAsync("DROP INDEX IF EXISTS ix ON items"));
        ownership.Message.ShouldContain("Sales"); ownership.Message.ShouldContain("DROP INDEX");
        (await RowsAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES")).ShouldHaveSingleItem().GetString(0).ShouldBe("ix");
        var path = new OqlPathExpression([new("a",null)]);
        var invalid = new OqlCreateIndexExpression("wide", "items", Enumerable.Repeat(path,33).ToArray(), false, false);
        var cap = Should.Throw<DatabaseException>(() => new DocumentPlanner(database.Catalog, context.Snapshot, null).Plan(invalid));
        cap.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC022");
    }

}
