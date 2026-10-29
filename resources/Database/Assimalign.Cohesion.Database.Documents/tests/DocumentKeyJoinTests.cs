using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentKeyJoinTests
{
    private readonly ITestOutputHelper _output;
    public DocumentKeyJoinTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Documents] - Key joins: direct AST requests require aliases on every SELECT scope")]
    public async Task ExecuteAsync_UnaliasedOuterAst_ShouldRefuseBeforeReadingAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-ast-aliases", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        var original = DocumentQueryRequest.FromOql("SELECT RAW (SELECT RAW b.n FROM [1] AS s JOIN items AS b ON META(b).id = 'a') FROM items AS a")
            .Statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        var query = new OqlSelectExpression(original.Collection, null, original.Projections, original.Predicate,
            original.GroupBy, original.Having, original.OrderBy, original.IsDistinct, original.IsValue, location: original.Location);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(query)), CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01035");
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Key joins: generated collections match a nested-loop oracle")]
    [InlineData("JOIN", false, false)]
    [InlineData("LEFT JOIN", true, false)]
    [InlineData("NEST", false, true)]
    [InlineData("LEFT NEST", true, true)]
    public async Task ExecuteAsync_GeneratedKeys_ShouldMatchOracleAsync(string form, bool left, bool nest)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-oracle", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("parents", CancellationToken.None);
        await session.CreateCollectionAsync("children", CancellationToken.None);
        var right = Enumerable.Range(0, 13).Select(index => (Id: "r" + index, N: index, Next: "r" + (index + 1))).ToArray();
        foreach (var item in right)
        { await session.ExecuteAsync("INSERT INTO children (KEY, VALUE) VALUES ('" + item.Id + "', {n:" + item.N + ",next:'" + item.Next + "'})", cancellationToken: CancellationToken.None); }
        var expected = new List<string>();
        var chained = new List<string>();
        for (int index = 0; index < 120; index++)
        {
            string? key = index % 7 == 0 ? null : "r" + index % 17;
            bool missing = index % 11 == 0;
            string document = "{n:" + index + (missing ? "" : ",k:" + (key is null ? "NULL" : "'" + key + "'")) + "}";
            await session.ExecuteAsync("INSERT INTO parents (KEY, VALUE) VALUES ('p" + index.ToString("D3", CultureInfo.InvariantCulture) + "', " + document + ")", cancellationToken: CancellationToken.None);
            var matches = right.Where(item => !missing && key == item.Id).ToArray();
            if (matches.Length == 0 && !left) { continue; }
            string body = matches.Length > 0 ? "{\"n\":" + matches[0].N + ",\"next\":\"" + matches[0].Next + "\"}" : "";
            string value = matches.Length > 0 ? nest ? "[" + body + "]" : body :
                nest && key is not null && !missing ? "[]" : "MISSING";
            expected.Add(index + ":" + value);
            var second = right.Where(item => matches.Any(first => first.Next == item.Id)).ToArray();
            foreach (int element in new[] { 1, 2 })
            { chained.Add(index + ":" + (second.Length == 0 ? "MISSING" : second[0].N.ToString(CultureInfo.InvariantCulture)) + ":" + element); }
        }
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT a.n, b FROM parents AS a " + form + " children AS b ON META(b).id = a.k ORDER BY a.n");
        // Assert
        rows.Select(row => row.GetValue(0) + ":" + (row.IsMissing(1) ? "MISSING" : ((JsonElement)row.GetValue(1)!).GetRawText())).ShouldBe(expected);
        session.LastQueryKeyLookups.ShouldBe(17);
        session.LastQueryDocumentsRead.ShouldBe(133);
        _output.WriteLine("Nested-loop oracle: 120 x 13 = 1560 comparisons; executor: 17 distinct key probes and 13 right-hand reads.");
        // Act: expand NEST or JOIN values, look up a key from that earlier binding,
        // and expand again. LEFT UNNEST preserves every unmatched outer tuple.
        string expanded = nest ? "b" : "CASE WHEN b IS MISSING THEN [] ELSE [b] END";
        var chainRows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT a.n AS parent_n, c.n AS child_n, u FROM parents AS a " + form + " children AS b ON META(b).id = a.k LEFT UNNEST " + expanded +
            " AS found LEFT JOIN children AS c ON META(c).id = found.next UNNEST [1,2] AS u ORDER BY a.n, u");
        // Assert
        chainRows.Select(row => row.GetValue(0) + ":" + (row.IsMissing(1) ? "MISSING" : Convert.ToString(row.GetValue(1), CultureInfo.InvariantCulture)) + ":" + row.GetValue(2)).ShouldBe(chained);
        session.LastQueryKeyLookups.ShouldBe(17);
        session.LastQueryDocumentsRead.ShouldBe(133);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Key joins: chains preserve metadata stars arrays and lexical captures")]
    [InlineData("SELECT META(a).id AS l, META(b).id AS r, META(b).version = META(a).version AS v FROM items AS a JOIN items AS b ON META(b).id = META(a).id ORDER BY l", "a|a|True;b|b|True;c|c|True")]
    [InlineData("SELECT RAW b.* FROM items AS a JOIN items AS b ON META(b).id = META(a).id ORDER BY a.n", "{\"n\":1};{\"n\":2};{\"n\":3}")]
    [InlineData("SELECT * FROM [1] AS a JOIN items AS b ON META(b).id = 'a'", "{\"a\":1,\"b\":{\"n\":1}}")]
    [InlineData("SELECT * FROM [1] AS a LEFT JOIN items AS b ON META(b).id = 'absent'", "{\"a\":1}")]
    [InlineData("SELECT b IS MISSING AS m, META(b).id AS id FROM [1] AS a LEFT JOIN items AS b ON META(b).id = 'absent'", "True|MISSING")]
    [InlineData("SELECT RAW c.n FROM items AS a JOIN items AS b ON META(b).id = META(a).id JOIN items AS c ON META(c).id = META(b).id ORDER BY c.n DESC LIMIT 1 OFFSET 1", "2")]
    [InlineData("SELECT RAW b.n FROM [1] AS a UNNEST ['a','b'] AS k JOIN items AS b ON META(b).id = k UNNEST [b.n] AS n ORDER BY n", "1;2")]
    [InlineData("SELECT RAW n.n FROM [1] AS a LEFT NEST items AS b ON META(b).id = 'b' UNNEST b AS n", "2")]
    [InlineData("SELECT b.n, COUNT(*) FROM items AS a JOIN items AS b ON META(b).id = 'a' GROUP BY b.n", "1|3")]
    [InlineData("SELECT RAW ARRAY_LENGTH(g) FROM items AS a JOIN items AS b ON META(b).id = 'a' GROUP BY b.n AS n GROUP AS g", "3")]
    [InlineData("SELECT RAW (SELECT RAW b.n FROM [1] AS s) FROM items AS a JOIN items AS b ON META(b).id = META(a).id ORDER BY a.n", "[1];[2];[3]")]
    [InlineData("SELECT RAW (SELECT RAW META(b).id FROM [1] AS s) FROM [1] AS a JOIN items AS b ON META(b).id = 'a'", "[\"a\"]")]
    [InlineData("SELECT RAW (SELECT RAW b.n FROM [1] AS s JOIN items AS b ON META(b).id = META(a).id) FROM items AS a ORDER BY a.n", "[1];[2];[3]")]
    [InlineData("SELECT RAW b.n FROM (SELECT RAW 'b' FROM items AS a LIMIT 1) AS k JOIN items AS b ON META(b).id = k", "2")]
    [InlineData("SELECT RAW b.n FROM [{key:'b'}] AS a JOIN items AS b ON META(b).id = key", "2")]
    [InlineData("SELECT RAW b.n FROM [{key:'B'}] AS a JOIN items AS b ON META(b).id = LOWER(key)", "2")]
    [InlineData("SELECT META(a).id AS id FROM [1] AS a JOIN items AS b ON META(b).id = 'a'", "")]
    [InlineData("SELECT META(b).id AS id FROM [1] AS a NEST items AS b ON META(b).id = 'a'", "")]
    [InlineData("SELECT RAW (SELECT b.* FROM [1] AS s LEFT JOIN items AS b ON META(b).id = 'absent') FROM [1] AS a", "[null]")]
    [InlineData("SELECT RAW (SELECT META(u).id AS id FROM [1] AS s) FROM [1] AS a LEFT UNNEST [] AS u", "[{\"id\":null}]")]
    [InlineData("SELECT RAW (SELECT META(b).id AS id FROM [1] AS s) FROM [1] AS a LEFT JOIN items AS b ON META(b).id = 'absent'", "[{}]")]
    [InlineData("SELECT RAW g FROM [1] AS a JOIN items AS b ON META(b).id = 'a' GROUP BY a GROUP AS g", "[{\"a\":1,\"b\":{\"n\":1}}]")]
    public async Task ExecuteAsync_SourceCompositions_ShouldPreserveBindingsAsync(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-composition", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        rows.ShouldNotBeEmpty();
        string.Join(";", rows.Select(row => string.Join("|", Enumerable.Range(0, row.FieldCount).Select(index => row.IsMissing(index) ? "MISSING" :
            row.GetValue(index) is JsonElement json ? json.GetRawText() : Convert.ToString(row.GetValue(index), CultureInfo.InvariantCulture))))).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Key joins: invalid references metadata and key types have located diagnostics")]
    [InlineData("SELECT b FROM items AS a JOIN items AS b ON META(b).id = b.k", "CD01035")]
    [InlineData("SELECT b FROM items AS a JOIN items AS b ON META(b).id = c.k JOIN items AS c ON META(c).id = 'a'", "CD01035")]
    [InlineData("SELECT META().id FROM items AS a JOIN items AS b ON META(b).id = 'a'", "CD01035")]
    [InlineData("SELECT b FROM items AS a JOIN items AS b ON META(b).id = ['a']", "CD02028")]
    [InlineData("SELECT b FROM items AS a JOIN items AS b ON META(b).id = 1", "CD02028")]
    [InlineData("SELECT b FROM items AS a JOIN items AS b ON META(b).id = @absent", "CD01005")]
    public async Task ExecuteAsync_InvalidJoin_ShouldRefuseBeforeResultAsync(string query, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-refusals", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        error.Diagnostics.Single().Start.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Key joins: repeated identities cache per statement and paging stops lookup work")]
    public async Task ExecuteAsync_CacheAndPaging_ShouldBoundReadsAsync()
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("join-work");
        builder.Functions.Add(DocumentScalarFunction.Create<string, string>("observe_key", key => { calls++; return key; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        string source = "[" + string.Join(",", Enumerable.Repeat("'a'", 100)) + "] AS k JOIN items AS b ON META(b).id = observe_key(k)";
        // Act / Assert
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW b.n FROM " + source)).Count.ShouldBe(100);
        session.LastQueryKeyLookups.ShouldBe(1);
        session.LastQueryDocumentsRead.ShouldBe(1);
        calls.ShouldBe(100);
        calls = 0;
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW b.n FROM " + source + " LIMIT 1 OFFSET 2")).Count.ShouldBe(1);
        session.LastQueryKeyLookups.ShouldBe(1);
        calls.ShouldBe(3);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM [1/0] AS a JOIN items AS b ON META(b).id = 'a' LIMIT 0")).ShouldBeEmpty();
        session.LastQueryKeyLookups.ShouldBe(0);
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT * FROM [1] AS a LEFT NEST items AS b ON META(b).id = 'a'");
        plan.ShouldContain(row => Equals(row.GetValue(2), "LeftKeyNest"));
        session.LastQueryDocumentsRead.ShouldBe(0);
        await session.ExecuteAsync("CREATE INDEX by_n ON items (n INCLUDE MISSING)", cancellationToken: CancellationToken.None);
        var orderedPlan = await DocumentSubqueryTests.RowsAsync(session,
            "EXPLAIN SELECT b.n FROM items AS a JOIN items AS b ON META(b).id = META(a).id ORDER BY a.n");
        orderedPlan.Select(row => row.GetValue(5) as string ?? string.Empty).ShouldContain(detail => detail.Contains("key_join_requires_sort", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Key joins: cache identity includes the collection and is shared across nested plans")]
    public async Task ExecuteAsync_CollectionIdentity_ShouldSeparateCachedDocumentsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-identities", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await session.CreateCollectionAsync("other", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO other (KEY, VALUE) VALUES ('a',{n:9}), ('nil',NULL)", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT b.n AS first_n, c.n AS second_n, (SELECT RAW d.n FROM [1] AS s JOIN items AS d ON META(d).id = 'a') AS nested FROM [1,2] AS a JOIN items AS b ON META(b).id = 'a' JOIN other AS c ON META(c).id = 'a'");
        // Assert
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(row => Equals(row.GetValue(0), 1m) && Equals(row.GetValue(1), 9m));
        rows.ShouldAllBe(row => ((JsonElement)row.GetValue(2)!)[0].GetInt32() == 1);
        session.LastQueryKeyLookups.ShouldBe(2);
        session.LastQueryDocumentsRead.ShouldBe(2);
        var nullDocument = (await DocumentSubqueryTests.RowsAsync(session,
            "SELECT b, META(b).id AS id FROM [1] AS a JOIN other AS b ON META(b).id = 'nil'")).Single();
        nullDocument.IsMissing(0).ShouldBeFalse();
        nullDocument.GetValue(0).ShouldBeNull();
        nullDocument.GetValue(1).ShouldBe("nil");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Key joins: INSERT SELECT and its EXPLAIN use the joined source plan")]
    public async Task ExecuteAsync_InsertSelect_ShouldUseSnapshotJoinedSourceAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("join-insert-source", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await session.CreateCollectionAsync("copied", CancellationToken.None);
        const string query = "INSERT INTO copied (KEY id, VALUE doc) SELECT META(b).id AS id, b AS doc FROM items AS a JOIN items AS b ON META(b).id = META(a).id";
        // Act
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + query);
        var result = await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        // Assert
        plan.ShouldContain(row => Equals(row.GetValue(2), "KeyJoin"));
        result.AffectedCount.ShouldBe(3);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW n FROM copied ORDER BY n")).Select(row => row.GetValue(0)).ShouldBe(new object?[] { 1m, 2m, 3m });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Key joins: work exhaustion after a cache hit releases cache retention")]
    public async Task ExecuteAsync_WorkBudget_ShouldCountCachedLookupsAndReleaseAsync()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("join-work-budget");
        builder.Options.StatementWorkBudget = 30;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        var request = DocumentQueryRequest.FromOql("SELECT RAW 1 FROM [1] AS a UNNEST [1,2,3,4,5,6,7,8,9,10,11,12] AS n JOIN items AS b ON META(b).id = 'a'");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05020");
            error.Diagnostics.Single().Start.ShouldNotBeNull();
            session.LastQueryKeyLookups.ShouldBe(1);
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Key joins: retained ordered rows exhaust the budget and release after failure")]
    public async Task ExecuteAsync_CacheBudget_ShouldRefuseAndReleaseAsync()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("join-budget");
        builder.Options.StatementMemoryBudget = 128 * 1024;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        string keys = "[" + string.Join(",", Enumerable.Range(0, 500).Select(index => "'absent" + index + "'")) + "]";
        var request = DocumentQueryRequest.FromOql("SELECT RAW 1 FROM " + keys + " AS k LEFT JOIN items AS b ON META(b).id = k ORDER BY k");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            session.LastQueryKeyLookups.ShouldBeGreaterThan(100);
            session.LastQueryKeyLookups.ShouldBeLessThan(500);
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }
}
