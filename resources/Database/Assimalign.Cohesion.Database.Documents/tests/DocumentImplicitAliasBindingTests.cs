using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentImplicitAliasBindingTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit alias: self-named fields remain reachable and ORDER BY projection aliases keep precedence")]
    [InlineData("SELECT RAW items.items.g FROM items ORDER BY items.g", "9;8;7")]
    [InlineData("SELECT RAW d.items.g FROM items d ORDER BY d.g", "9;8;7")]
    [InlineData("SELECT x AS items FROM items ORDER BY items", "1;2;3")]
    [InlineData("SELECT RAW items.g FROM items GROUP BY g ORDER BY items.g", "1;2;3")]
    [InlineData("SELECT DISTINCT g FROM items ORDER BY items.g", "1;2;3")]
    [InlineData("SELECT RAW g FROM items ORDER BY items.g", "1;2;3")]
    [InlineData("SELECT RAW (SELECT RAW i.g FROM items i ORDER BY i.g) FROM items", "[1,2,3];[1,2,3];[1,2,3]")]
    [InlineData("SELECT RAW (SELECT RAW i.g FROM items i) FROM items AS items", "[1];[2];[3]")]
    [InlineData("SELECT RAW b.g FROM items JOIN items AS b ON META(b).id = 'a'", "1;1;1")]
    [InlineData("SELECT RAW items.g FROM items AS a JOIN items ON META(items).id = 'a'", "1;1;1")]
    [InlineData("SELECT RAW tags FROM items UNNEST items.tags JOIN items AS b ON META(b).id = 'a'", "1;2;1;2;1;2")]
    public async Task ExecuteAsync_SourceBindings_ShouldResolveConsistentlyAsync(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-bindings", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{g:1,x:3,items:{g:9},tags:[1,2]}), ('b',{g:2,x:1,items:{g:8},tags:[1,2]}), ('c',{g:3,x:2,items:{g:7},tags:[1,2]})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        string.Join(';', rows.Select(row => row.GetValue(0)?.ToString())).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit alias: root mutation and duplicate source names are refused")]
    [InlineData("UPDATE items SET items = 1", "CD02016")]
    [InlineData("UPDATE items UNSET items", "CD02016")]
    [InlineData("UPDATE items SET items.g = 1, g = 2", "CD01017")]
    [InlineData("SELECT * FROM items JOIN items ON META(items).id = 'a'", "CD00010")]
    [InlineData("SELECT * FROM items UNNEST tags items", "CD00010")]
    public async Task ExecuteAsync_InvalidBinding_ShouldPreserveDiagnosticAsync(string statement, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-refusals", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit alias: duplicate direct AST names keep the binding diagnostic")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_DuplicateAstBinding_ShouldRefuseAsync(bool join, bool emptyAlias)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-ast", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        string variable = emptyAlias ? "other" : "items";
        OqlSourceBindingExpression binding = join
            ? new OqlJoinExpression("items", variable, new OqlLiteralExpression("a"), false, false)
            : new OqlUnnestExpression(new OqlPathExpression([new OqlPathSegment("tags", null)]), variable, false);
        var select = new OqlSelectExpression("items", emptyAlias ? "" : null, [new OqlProjection(new OqlStarExpression(), null)], null, [], null, [], false, false, bindings: [binding]);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(select)), CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01023");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit alias: quoted stored names stay opaque when they contain dots")]
    [InlineData("items.dot")]
    [InlineData("cohesion_schema.custom")]
    public async Task ExecuteAsync_QuotedStoredName_ShouldKeepCompleteAliasAsync(string name)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-opaque", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string quoted = "\"" + name + "\"";
        await session.ExecuteAsync("CREATE COLLECTION " + quoted, cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO " + quoted + " (KEY, VALUE) VALUES ('a',{g:1,tags:[2]})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW " + quoted + ".g FROM " + quoted + " UNNEST tags custom");
        // Assert
        rows.ShouldHaveSingleItem().GetValue(0).ShouldBe(1m);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Implicit alias: predicate and ordering paths use root indexes and EXPLAIN prints normalized paths")]
    public async Task ExecuteAsync_QualifiedIndexPaths_ShouldChooseRootKeysAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-index", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{status:'x',score:2,items:{status:'y',score:0}}), ('b',{status:'y',score:1,items:{status:'x',score:9}})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_status ON items(status)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_score ON items(score INCLUDE MISSING)", cancellationToken: CancellationToken.None);
        // Act
        var seek = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT RAW META().id FROM items WHERE items.status = 'x'");
        var ordered = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT RAW META().id FROM items ORDER BY items.score");
        // Assert
        seek.ShouldContain(row => row.GetString(2) == "IndexSeek" && row.GetString(4) == "by_status");
        ordered.ShouldContain(row => row.GetString(4) == "by_score" && row.GetString(5)!.Contains("consumed=True; sort=elided", StringComparison.Ordinal));
        ordered.Select(row => row.GetString(5)).OfType<string>().ShouldContain(detail => detail.Contains("score ASC", StringComparison.Ordinal) && !detail.Contains("items.score", StringComparison.Ordinal));
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW META(items).id FROM items WHERE items.status = 'x'")).ShouldHaveSingleItem().GetString(0).ShouldBe("a");
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW META(items).id FROM items ORDER BY items.score")).Select(row => row.GetString(0)).ShouldBe(new[] { "b", "a" });
        (await DocumentSubqueryTests.RowsAsync(session, "UPDATE items SET items.score = 3 WHERE items.status = 'x' RETURNING RAW META(items).id")).ShouldHaveSingleItem().GetString(0).ShouldBe("a");
        (await DocumentSubqueryTests.RowsAsync(session, "DELETE FROM items WHERE items.status = 'x' RETURNING RAW META(items).id")).ShouldHaveSingleItem().GetString(0).ShouldBe("a");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit alias: system sources use the final segment with its written case")]
    [InlineData("cohesion_schema.indexes", "indexes")]
    [InlineData("COHESION_SCHEMA.INDEXES", "INDEXES")]
    [InlineData("\"cohesion_schema.indexes\"", "indexes")]
    public async Task ExecuteAsync_SystemSource_ShouldUseLastSegmentAsync(string source, string name)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-system", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON items(g)", cancellationToken: CancellationToken.None);
        // Act
        var paths = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW " + name + ".INDEX_NAME FROM " + source + " GROUP BY INDEX_NAME");
        var star = await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM " + source + " UNNEST PATHS p");
        var group = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW g FROM " + source + " GROUP BY INDEX_NAME GROUP AS g");
        var capture = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW (SELECT RAW " + name + ".INDEX_NAME FROM [1] AS s) FROM " + source);
        // Assert
        paths.ShouldHaveSingleItem().GetString(0).ShouldBe("ix");
        star.ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<JsonElement>().GetProperty(name).GetProperty("INDEX_NAME").GetString().ShouldBe("ix");
        group.ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<JsonElement>()[0].GetProperty(name).GetProperty("INDEX_NAME").GetString().ShouldBe("ix");
        capture.ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("[\"ix\"]");
    }
}
