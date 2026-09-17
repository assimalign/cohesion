using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentQueryTests
{
    [Fact]
    public async Task Nested_paths_arrays_and_missing_fields_keep_document_shapes()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Put(collection, session, "c", "{\"name\":\"third\",\"profile\":null,\"tags\":[]}");
        await Put(collection, session, "b", "{\"name\":\"second\",\"other\":true}");
        await Put(collection, session, "a", "{\"name\":\"first\",\"profile\":{\"city\":\"Paris\"},\"tags\":[\"blue\",{\"kind\":\"nested\"}]}");
        var rows = await Rows(session, "SELECT d.name, d.profile.city AS city, d.tags[1].kind AS kind FROM items AS d");
        rows.Select(row => row.GetString(0)).ShouldBe(["first", "second", "third"]);
        rows[0].GetString(1).ShouldBe("Paris");
        rows[0].GetString(2).ShouldBe("nested");
        rows[1].IsNull(1).ShouldBeTrue();
        rows[2].IsNull(2).ShouldBeTrue();
        var nested = await Rows(session, "SELECT profile, tags FROM items WHERE name = 'first'");
        ((JsonElement)nested[0].GetValue(0)!).GetProperty("city").GetString().ShouldBe("Paris");
        ((JsonElement)nested[0].GetValue(1)!)[1].GetProperty("kind").GetString().ShouldBe("nested");
        var whole = await Rows(session, "SELECT * FROM items WHERE tags[0] = 'blue'");
        ((JsonElement)whole.Single().GetValue(0)!).GetProperty("name").GetString().ShouldBe("first");
    }

    [Theory]
    [InlineData(65)]
    [InlineData(128)]
    public async Task Query_parsing_accepts_the_same_document_depth_as_storage(int depth)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        string content = "42";
        for (int i = 0; i < depth; i++) { content = "{\"x\":" + content + "}"; }
        string path = string.Join('.', Enumerable.Repeat("x", depth));
        await Put(collection, session, "deep", content);
        var whole = ((JsonElement)(await Rows(session, "SELECT * FROM items")).Single().GetValue(0)!);
        for (int i = 0; i < depth; i++) { whole = whole.GetProperty("x"); }
        whole.GetInt32().ShouldBe(42);
        string query = $"SELECT d.{path} AS value FROM items AS d WHERE d.{path} = 42";
        (await Rows(session, query)).Single().GetInt32(0).ShouldBe(42);
        await database.CreateIndexAsync("items", "deep_value", path);
        (await Plan(database, query)).Access.ShouldBeOfType<DocumentIndexPath>();
        (await Rows(session, query)).Single().GetInt32(0).ShouldBe(42);
    }

    [Fact]
    public async Task Mixed_shape_grouping_aggregates_and_having_are_deterministic()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Put(collection, session, "d", "{\"category\":\"b\",\"amount\":4}");
        await Put(collection, session, "c", "{\"category\":\"a\"}");
        await Put(collection, session, "b", "{\"category\":\"a\",\"amount\":6}");
        await Put(collection, session, "a", "{\"category\":\"a\",\"amount\":2}");
        await Put(collection, session, "e", "{\"amount\":9}");
        var rows = await Rows(session, "SELECT category, COUNT(*) AS count, COUNT(amount) AS present, SUM(amount) AS total, AVG(amount) AS average, MIN(amount) AS minimum, MAX(amount) AS maximum FROM items GROUP BY category HAVING COUNT(*) > 1 ORDER BY total DESC");
        rows.Count.ShouldBe(1);
        rows[0].GetString(0).ShouldBe("a");
        Enumerable.Range(1, 6).Select(rows[0].GetInt32).ShouldBe([3, 2, 8, 4, 2, 6]);
        var groups = await Rows(session, "SELECT category, SUM(amount) AS total FROM items GROUP BY category");
        groups[0].IsNull(0).ShouldBeTrue();
        groups.Skip(1).Select(row => row.GetString(0)).ShouldBe(["a", "b"]);
        var empty = await Rows(session, "SELECT COUNT(*) AS count, SUM(amount) AS total FROM items WHERE amount > 100");
        empty.Single().GetInt64(0).ShouldBe(0L);
        empty.Single().IsNull(1).ShouldBeTrue();
    }

    [Fact]
    public async Task Arrays_objects_and_mixed_scalar_types_have_structural_group_keys()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Put(collection, session, "a", "{\"key\":{\"a\":1,\"b\":[2,3]}}");
        await Put(collection, session, "b", "{\"key\":{\"b\":[2.0,3],\"a\":1.0}}");
        await Put(collection, session, "c", "{\"key\":[1,2]}");
        await Put(collection, session, "d", "{\"key\":\"1\"}");
        await Put(collection, session, "e", "{\"key\":1}");
        var rows = await Rows(session, "SELECT key, COUNT(*) AS count FROM items GROUP BY key");
        rows.Count.ShouldBe(4);
        rows.Select(row => row.GetInt32(1)).ShouldBe([1, 1, 1, 2]);
        rows[0].GetValue(0).ShouldBe(1m);
        rows[1].GetValue(0).ShouldBe("1");
    }

    [Fact]
    public async Task Grouping_resolves_iteration_aliases_and_preserves_constant_scalar_types()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Put(collection, session, "a", "{\"category\":\"b\",\"amount\":3,\"key\":1}");
        await Put(collection, session, "b", "{\"category\":\"a\",\"amount\":2,\"key\":\"1\"}");
        await Put(collection, session, "c", "{\"category\":\"a\",\"amount\":4,\"key\":2}");
        var rows = await Rows(session, "SELECT d.category AS label, SUM(d.amount) AS total FROM items AS d GROUP BY category HAVING SUM(amount) > 1 ORDER BY label, total");
        rows.Select(row => row.GetString(0)).ShouldBe(["a", "b"]);
        rows.Select(row => row.GetInt32(1)).ShouldBe([6, 3]);
        // The group predicate key = 1 merges string '1' and numeric 2 into
        // the false group. key = '1' cannot choose one representative value.
        await Should.ThrowAsync<DatabaseException>(async () => await Rows(session,
            "SELECT key = '1' AS category FROM items GROUP BY key = 1"));
    }

    [Theory]
    [InlineData("score = 2", true)]
    [InlineData("score >= 2 AND score < 4", false)]
    [InlineData("2 <= score AND 4 > score", false)]
    [InlineData("score >= @minimum", false)]
    public async Task Eligible_equality_and_ranges_use_indexes_and_match_scan_results(string predicate, bool equality)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Put(collection, session, "d", "{\"name\":\"four\",\"score\":4}");
        await Put(collection, session, "c", "{\"name\":\"three\",\"score\":3}");
        await Put(collection, session, "b", "{\"name\":\"two\",\"score\":2.0}");
        await Put(collection, session, "a", "{\"name\":\"one\",\"score\":1}");
        await Put(collection, session, "e", "{\"name\":\"text\",\"score\":\"2\"}");
        await Put(collection, session, "f", "{\"name\":\"missing\"}");
        await Put(collection, session, "g", "{\"name\":\"null\",\"score\":null}");
        await Put(collection, session, "h", "{\"name\":\"bool\",\"score\":true}");
        await Put(collection, session, "i", "{\"name\":\"array\",\"score\":[2]}");
        await Put(collection, session, "j", "{\"name\":\"object\",\"score\":{\"value\":2}}");
        string query = $"SELECT name, score FROM items WHERE {predicate}";
        var parameters = new Dictionary<string, object?> { ["minimum"] = 2 };
        var before = await Rows(session, query, parameters);
        await database.CreateIndexAsync("items", "by_score", "score");
        var plan = await Plan(database, query, parameters);
        var seek = plan.Access.ShouldBeOfType<DocumentIndexPath>();
        seek.Index.Name.ShouldBe("by_score");
        if (equality) { seek.Lower.ShouldBe(seek.Upper); }
        var after = await Rows(session, query, parameters);
        after.Select(row => row.GetString(0)).ShouldBe(before.Select(row => row.GetString(0)));
        after.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Nested_array_indexes_remain_correct_after_replace_delete_and_rollback()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await database.CreateIndexAsync("items", "by_nested", "values[0].number");
        await Put(collection, session, "a", "{\"name\":\"a\",\"values\":[{\"number\":1}]}");
        await Put(collection, session, "b", "{\"name\":\"b\",\"values\":[{\"number\":2}]}");
        const string query = "SELECT name FROM items AS d WHERE d.values[0].number = 2";
        (await Plan(database, query)).Access.ShouldBeOfType<DocumentIndexPath>();
        (await Rows(session, query)).Select(row => row.GetString(0)).ShouldBe(["b"]);
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await Put(collection, session, "a", "{\"name\":\"a\",\"values\":[{\"number\":2}]}");
            await collection.DeleteAsync(session, "b");
            (await Rows(session, query)).Select(row => row.GetString(0)).ShouldBe(["a"]);
            await transaction.RollbackAsync();
        }
        (await Rows(session, query)).Select(row => row.GetString(0)).ShouldBe(["b"]);
        await Put(collection, session, "b", "{\"name\":\"b\",\"values\":[]}");
        (await Rows(session, query)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("score >= @minimum", "\uD800\uDC00")]
    [InlineData("score < @minimum", "\uE000")]
    [InlineData("score = @minimum", "\uD800\uDC00")]
    public async Task String_index_order_matches_ordinal_utf16_for_supplementary_characters(string predicate, string minimum)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Put(collection, session, "a", "{\"score\":\"a\"}");
        await Put(collection, session, "b", "{\"score\":\"\\uD800\\uDC00\"}");
        await Put(collection, session, "c", "{\"score\":\"\\uE000\"}");
        await Put(collection, session, "d", "{\"score\":1}");
        await Put(collection, session, "e", "{\"score\":true}");
        var parameters = new Dictionary<string, object?> { ["minimum"] = minimum };
        string query = $"SELECT score FROM items WHERE {predicate} ORDER BY score";
        var before = await Rows(session, query, parameters);
        await database.CreateIndexAsync("items", "by_score", "score");
        (await Plan(database, query, parameters)).Access.ShouldBeOfType<DocumentIndexPath>();
        var after = await Rows(session, query, parameters);
        after.Select(row => row.GetString(0)).ShouldBe(before.Select(row => row.GetString(0)));
        after.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Boolean_index_ranges_do_not_match_other_scalar_domains()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Put(collection, session, "a", "{\"score\":false}");
        await Put(collection, session, "b", "{\"score\":true}");
        await Put(collection, session, "c", "{\"score\":1}");
        await Put(collection, session, "d", "{\"score\":\"true\"}");
        const string query = "SELECT score FROM items WHERE score > FALSE";
        var before = await Rows(session, query);
        await database.CreateIndexAsync("items", "by_score", "score");
        (await Plan(database, query)).Access.ShouldBeOfType<DocumentIndexPath>();
        var after = await Rows(session, query);
        after.Select(row => row.GetValue(0)).ShouldBe(before.Select(row => row.GetValue(0)));
        after.Single().GetBoolean(0).ShouldBeTrue();
    }

    [Fact]
    public async Task Null_missing_and_or_predicates_keep_scan_semantics_when_indexes_exist()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Put(collection, session, "a", "{\"name\":\"null\",\"score\":null}");
        await Put(collection, session, "b", "{\"name\":\"missing\"}");
        await Put(collection, session, "c", "{\"name\":\"number\",\"score\":1}");
        await database.CreateIndexAsync("items", "by_score", "score");
        const string query = "SELECT name FROM items WHERE score IS NULL OR score = 1";
        (await Plan(database, query)).Access.ShouldBeOfType<DocumentScanPath>();
        (await Rows(session, query)).Select(row => row.GetString(0)).ShouldBe(["null", "missing", "number"]);
        (await Rows(session, "SELECT name FROM items WHERE score = NULL")).ShouldBeEmpty();
        (await Rows(session, "SELECT name FROM items WHERE NOT (score IS NULL)")).Single().GetString(0).ShouldBe("number");
    }

    [Fact]
    public async Task Ordering_ties_follow_identity_and_scalar_documents_round_trip()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Put(collection, session, "c", "[1,{\"a\":true}]");
        await Put(collection, session, "b", "\"text\"");
        await Put(collection, session, "a", "42");
        var rows = await Rows(session, "SELECT * FROM items ORDER BY 1 DESC");
        rows[0].GetValue(0).ShouldBe(42m);
        rows[1].GetValue(0).ShouldBe("text");
        ((JsonElement)rows[2].GetValue(0)!)[1].GetProperty("a").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("SELECT amount, SUM(amount) FROM items")]
    [InlineData("SELECT SUM(COUNT(*)) FROM items")]
    [InlineData("SELECT name FROM items WHERE COUNT(*) > 1")]
    [InlineData("SELECT name FROM items HAVING name = 'a'")]
    public async Task Invalid_aggregate_contexts_are_rejected_before_execution(string query)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(DocumentQueryRequest.FromOql(query)));
    }

    [Fact]
    public async Task Direct_requests_cannot_bypass_parse_diagnostics()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT * FROM items LIMIT 1");
        var exception = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(new DocumentQueryRequest(statement)));
        exception.Message.ShouldContain("COHDBL001");
    }

    private static ValueTask<Document> Put(IDocumentCollection collection, IDatabaseSession session, string id, string json)
        => collection.PutAsync(session, id, Encoding.UTF8.GetBytes(json));

    private static async Task<List<QueryRow>> Rows(IDatabaseSession session, string query, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var result = (QueryResultSet)await session.ExecuteAsync(DocumentQueryRequest.FromOql(query, parameters));
        await using (result)
        {
            var rows = new List<QueryRow>();
            await foreach (var row in result.GetRowsAsync()) { rows.Add(row); }
            return rows;
        }
    }

    private static ValueTask<DocumentPlan> Plan(IDocumentDatabase database, string query, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var instance = (DocumentDatabaseInstance)database;
        return instance.RunAsync(null, operation => new ValueTask<DocumentPlan>(new DocumentPlanner(instance.Catalog,
            operation.Context.Snapshot, parameters).Plan(DocumentQueryRequest.FromOql(query).Statement.OqlExpression)), CancellationToken.None);
    }
}
