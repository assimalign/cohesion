using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentQueryTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Group aliases: quoted whitespace names preserve the existing identifier rule")]
    public async Task Execute_QuotedWhitespaceGroupNames_ShouldBindExactlyAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("quoted-group-bindings", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{k:1})", cancellationToken: CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, "SELECT \" \" AS key, \"  \" AS members FROM items GROUP BY k AS \" \" GROUP AS \"  \"")).ShouldHaveSingleItem();
        // Assert
        row.GetValue(0).ShouldBe(1m);
        var wrapper = row.GetValue(1).ShouldBeOfType<JsonElement>()[0];
        wrapper.GetProperty(" ").GetDecimal().ShouldBe(1m);
        wrapper.GetProperty("items").GetProperty("k").GetDecimal().ShouldBe(1m);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - GROUP AS: binding wrappers and group aliases work across projection HAVING and ordering")]
    public async Task Execute_GroupAs_ShouldRetainEveryBindingAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("group-bindings", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{k:'x',name:'alpha',tags:[1,2]}),('b',{k:'x',name:'beta',tags:[]}),('c',{k:'y',name:'gamma',tags:[NULL]})", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "SELECT bucket, g, ARRAY r.d.name FOR r IN g END AS names, COUNT(bucket) AS n FROM items d LEFT UNNEST d.tags t GROUP BY UPPER(d.k) AS bucket GROUP AS g HAVING ANY r IN g SATISFIES r.d.name = 'alpha' END ORDER BY ARRAY_LENGTH(g) DESC");

        // Assert: member ordering is deliberately not part of GROUP AS's contract.
        var row = rows.ShouldHaveSingleItem();
        row.GetString(0).ShouldBe("X");
        row.GetValue(3).ShouldBe(3L);
        var wrappers = row.GetValue(1).ShouldBeOfType<JsonElement>();
        wrappers.GetArrayLength().ShouldBe(3);
        foreach (var wrapper in wrappers.EnumerateArray())
        {
            wrapper.GetProperty("bucket").GetString().ShouldBe("X");
            wrapper.GetProperty("d").GetProperty("k").GetString().ShouldBe("x");
        }
        wrappers.EnumerateArray().Count(x => x.TryGetProperty("t", out _)).ShouldBe(2);
        row.GetValue(2).ShouldBeOfType<JsonElement>().EnumerateArray().Select(x => x.GetString()).Order().ShouldBe(["alpha", "alpha", "beta"]);
        await session.ExecuteAsync("DELETE FROM items", cancellationToken: CancellationToken.None);
        wrappers.EnumerateArray().First().GetProperty("d").GetProperty("name").GetString().ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - GROUP AS: MISSING and NULL group aliases and bindings retain their distinct shapes")]
    public async Task Execute_GroupAsMissing_ShouldOmitOnlyMissingMembersAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("missing-group-bindings", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{}),('b',{k:NULL,tags:[NULL]})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "SELECT key, g FROM items d LEFT UNNEST d.tags t GROUP BY d.k key GROUP AS g ORDER BY key");
        // Assert
        rows.Count.ShouldBe(2);
        rows[0].IsMissing(0).ShouldBeTrue();
        var absent = rows[0].GetValue(1).ShouldBeOfType<JsonElement>()[0];
        absent.TryGetProperty("key", out _).ShouldBeFalse();
        absent.TryGetProperty("t", out _).ShouldBeFalse();
        absent.GetProperty("d").ValueKind.ShouldBe(JsonValueKind.Object);
        rows[1].IsNull(0).ShouldBeTrue();
        var nulls = rows[1].GetValue(1).ShouldBeOfType<JsonElement>()[0];
        nulls.GetProperty("key").ValueKind.ShouldBe(JsonValueKind.Null);
        nulls.GetProperty("t").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - GROUP AS: implicit source names wrap scalar roots and empty input produces no groups")]
    public async Task Execute_ImplicitGroupBinding_ShouldKeepRootsAndEmptyMetadataAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("scalar-group-bindings", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',NULL),('b',42),('c',[1])", cancellationToken: CancellationToken.None);
        // Act
        var wrappers = (await RowsAsync(session, "SELECT VALUE g FROM items GROUP BY TRUE GROUP AS g")).ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<JsonElement>();
        // Assert
        wrappers.EnumerateArray().Select(x => x.GetProperty("items").ValueKind).Order().ShouldBe(new[] { JsonValueKind.Null, JsonValueKind.Number, JsonValueKind.Array }.Order());
        await using var empty = (await session.ExecuteAsync("SELECT g FROM items WHERE FALSE GROUP BY TRUE GROUP AS g", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        empty.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Json);
        int count = 0;
        await foreach (var _ in empty.GetRowsAsync(CancellationToken.None)) { count++; }
        count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Group aliases: lexical shadowing and aggregate row inputs preserve binding scopes")]
    public async Task Execute_GroupAliases_ShouldRespectLexicalScopesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("group-scopes", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{k:2,g:'source'}),('b',{k:2,g:'source'})", cancellationToken: CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, "SELECT key + 1 AS next, SUM(key) AS total, ARRAY key FOR key IN [7] END AS local_key, FIRST r.d.k FOR r IN g END AS first_key, ANY g IN [TRUE] SATISFIES g END AS local_g FROM items d WHERE g = 'source' GROUP BY d.k AS key GROUP AS g HAVING ARRAY_LENGTH(g) = 2 ORDER BY key")).ShouldHaveSingleItem();
        // Assert
        row.GetValue(0).ShouldBe(3m);
        row.GetValue(1).ShouldBe(4m);
        row.GetValue(2).ShouldBeOfType<JsonElement>()[0].GetDecimal().ShouldBe(7m);
        row.GetValue(3).ShouldBe(2m);
        row.GetValue(4).ShouldBe(true);
        (await RowsAsync(session, "SELECT key, COUNT(*) AS n FROM items GROUP BY k key HAVING key = 2 ORDER BY key")).ShouldHaveSingleItem().GetValue(0).ShouldBe(2m);
        (await RowsAsync(session, "SELECT SUM(ARRAY_LENGTH(ARRAY g FOR g IN [1] END)) AS n FROM items GROUP BY TRUE GROUP AS g")).ShouldHaveSingleItem().GetValue(0).ShouldBe(2m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - GROUP AS: invalid declarations and aggregate visibility preserve coded binding failures")]
    [InlineData("SELECT g FROM items d GROUP BY k GROUP AS d", "CD01030")]
    [InlineData("SELECT g FROM items d GROUP BY k AS d GROUP AS g", "CD01030")]
    [InlineData("SELECT g FROM items GROUP BY k AS x, k AS x GROUP AS g", "CD01030")]
    [InlineData("SELECT g FROM items GROUP BY k AS g GROUP AS g", "CD01030")]
    [InlineData("SELECT ARRAY_AGG(g) FROM items GROUP BY k GROUP AS g", "CD01031")]
    [InlineData("SELECT d.name FROM items d GROUP BY k GROUP AS g", "CD05001")]
    [InlineData("SELECT DISTINCT x FROM items d GROUP BY d.x, d.k AS x ORDER BY d.x", "CD01014")]
    public async Task Execute_InvalidGroupBinding_ShouldRefuseAsync(string query, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("invalid-group-bindings", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        failure.Diagnostics[0].Start.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - GROUP AS: EXPLAIN describes bindings without reading or evaluating group terms")]
    public async Task Execute_GroupExplain_ShouldDescribeBindingsWithoutExecutionAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("explain-group-bindings", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        await using var result = (await session.ExecuteAsync("EXPLAIN SELECT g, key FROM items GROUP BY 1/0 AS key GROUP AS g", cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        // Assert
        var details = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { if (row.GetValue(5) is string detail) { details.Add(detail); } }
        details.ShouldContain("group_as=g; group_aliases=key");
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Group aliases: malformed direct AST declarations refuse before input execution")]
    [InlineData("empty")]
    [InlineData("count")]
    [InlineData("no-keys")]
    public void Plan_InvalidGroupingAst_ShouldRefuseBinding(string scenario)
    {
        // Arrange
        var query = ((OqlQueryStatement)new OqlQueryParser().Parse("SELECT COUNT(*) FROM items GROUP BY k")).OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        var direct = new OqlSelectExpression(query.Collection, query.Alias, query.Projections, query.Predicate,
            scenario == "no-keys" ? [] : query.GroupBy, query.Having, query.OrderBy, false, false,
            groupAliases: scenario == "empty" ? [""] : ["a", "b"], groupAs: "g");
        // Act / Assert
        Should.Throw<DatabaseException>(() => DocumentPlanner.CreateLogicalPlan(direct)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01030");
    }
}
