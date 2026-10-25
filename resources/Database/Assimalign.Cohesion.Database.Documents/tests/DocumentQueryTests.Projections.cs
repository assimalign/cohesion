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

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentQueryTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - DISTINCT uses structural collation and preserves first occurrences")]
    public async Task Execute_DistinctValues_ShouldUseCollationAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("projection", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        string[] values = ["{\"a\":null,\"b\":[2]}", "{\"b\":[2.0],\"a\":null}", "[2,null]", "[2.0,null]", "2", "2.0", "null", "null"];
        for (int i = 0; i < values.Length; i++) { await PutAsync(collection, session, i.ToString(), "{\"x\":" + values[i] + "}"); }
        // Act
        var rows = await RowsAsync(session, "SELECT DISTINCT VALUE x FROM items");
        // Assert
        rows.Count.ShouldBe(4);
        rows[0].GetValue(0).ShouldBeOfType<JsonElement>().GetRawText().ShouldBe(values[0]);
        rows[1].GetValue(0).ShouldBeOfType<JsonElement>().GetRawText().ShouldBe(values[2]);
        rows[2].GetValue(0).ShouldBe(2m);
        rows[3].IsNull(0).ShouldBeTrue();
        (await RowsAsync(session, "SELECT DISTINCT {'a': NULL} AS o FROM items")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT DISTINCT COUNT(*) AS n FROM items GROUP BY x ORDER BY n")).Count.ShouldBe(1);
        var ordered = await RowsAsync(session, "SELECT DISTINCT x AS n FROM items ORDER BY n");
        ordered[0].IsNull(0).ShouldBeTrue();
        ordered[1].GetValue(0).ShouldBe(2m);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, "SELECT DISTINCT x FROM items ORDER BY META().id"));
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD01014");
        diagnostic.Start.ShouldBe(38);
        diagnostic.End.ShouldBe(47);
        diagnostic.Line.ShouldBe(1);
        error.Message.ShouldBe("SELECT DISTINCT requires every ORDER BY expression to appear in the projection.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Constructors execute in grouped projections, predicates and INSERT")]
    public async Task Execute_Constructors_ShouldComposeWithExpressionsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("projection", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"category\":\"a\",\"loc\":{\"x\":1},\"name\":\"alpha\"}");
        await PutAsync(collection, session, "b", "{\"category\":\"a\",\"name\":\"beta\"}");
        await PutAsync(collection, session, "c", "{\"category\":\"b\",\"name\":\"gamma\"}");
        // Act / Assert
        (await RowsAsync(session, "SELECT DISTINCT name AS n FROM items ORDER BY n"))
            .Select(row => row.GetString(0)).ShouldBe(["alpha", "beta", "gamma"]);
        var grouped = await RowsAsync(session, "SELECT VALUE {'category': category, 'total': COUNT(*)} FROM items GROUP BY category ORDER BY category");
        grouped.Select(row => row.GetString(0)).ShouldBe(["{\"category\":\"a\",\"total\":2}", "{\"category\":\"b\",\"total\":1}"]);
        (await RowsAsync(session, "SELECT name FROM items d WHERE d.loc = {x: 1}")).ShouldHaveSingleItem().GetString(0).ShouldBe("alpha");
        var number = (await RowsAsync(session, "SELECT VALUE {a: 1.50, b: 79228162514264337593543950335} FROM items WHERE name = 'alpha'"))
            .ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<JsonElement>();
        number.GetProperty("a").GetRawText().ShouldBe("1.50");
        number.GetProperty("b").GetDecimal().ShouldBe(decimal.MaxValue);
        var missing = (await RowsAsync(session, "SELECT VALUE {a: MISSING, b: [MISSING, NULL, {x: 1}]} FROM items WHERE name = 'alpha'"))
            .ShouldHaveSingleItem().GetString(0);
        missing.ShouldBe("{\"b\":[null,null,{\"x\":1}]}");
        (await RowsAsync(session, "SELECT [COUNT(*), category] AS pair FROM items GROUP BY category ORDER BY [COUNT(*), category]"))
            .Select(row => row.GetString(0)).ShouldBe(["[1,\"b\"]", "[2,\"a\"]"]);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('array', [1, MISSING, {x: 2}])", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT * FROM items WHERE META().id = 'array'")).ShouldHaveSingleItem().GetString(0).ShouldBe("[1,null,{\"x\":2}]");
        await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, "SELECT [name, COUNT(*)] FROM items"));
        await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, "SELECT [META(other).id] FROM items d"));
        await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, "SELECT [@missing] FROM items WHERE FALSE"));
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Lone star row writer preserves every root byte for byte")]
    [InlineData(" {\"a\": 1} ")]
    [InlineData(" [1, 2] ")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task Write_LoneStar_ShouldPreserveStoredBytesAsync(string content)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("projection", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", content);
        var set = (await session.ExecuteAsync("SELECT * FROM items", cancellationToken: CancellationToken.None)).ShouldBeOfType<DocumentQueryResult>();
        using var writer = new DocumentRowWriter();
        // Act / Assert
        int count = 0;
        await foreach (var row in set.GetRowsAsync(CancellationToken.None))
        {
            count++;
            var bytes = writer.Write(row, set.Columns, set.BareValue);
            Encoding.UTF8.GetString(bytes.Span).ShouldBe(content);
            row.GetBytes(0).ToArray().ShouldBe(Encoding.UTF8.GetBytes(content));
            using var json = JsonDocument.Parse(bytes);
        }
        count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Row writer emits one ordered JSON value")]
    [InlineData("SELECT name AS a, NULL AS b, MISSING AS c FROM items", "{\"a\":\"alpha\",\"b\":null}")]
    [InlineData("SELECT VALUE name FROM items", "\"alpha\"")]
    [InlineData("SELECT VALUE MISSING FROM items", "null")]
    [InlineData("SELECT *, name FROM items", "{\"document\":{\"name\":\"alpha\"},\"name\":\"alpha\"}")]
    public async Task Write_Projection_ShouldEncodeOneValueAsync(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("projection", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"name\":\"alpha\"}");
        var set = (await session.ExecuteAsync(query, cancellationToken: CancellationToken.None)).ShouldBeOfType<DocumentQueryResult>();
        using var writer = new DocumentRowWriter();
        // Act / Assert
        int count = 0;
        await foreach (var row in set.GetRowsAsync(CancellationToken.None))
        {
            count++;
            Encoding.UTF8.GetString(writer.Write(row, set.Columns, set.BareValue).Span).ShouldBe(expected);
        }
        count.ShouldBe(1);
        if (set.BareValue) { set.Columns.ShouldHaveSingleItem().Name.ShouldBe("value"); }
    }
    [Theory(DisplayName = "Cohesion Test [Documents] - Direct VALUE ASTs reject multiple expressions, aliases and star")]
    [InlineData("multiple")]
    [InlineData("alias")]
    [InlineData("star")]
    public async Task Execute_InvalidValueAst_ShouldRejectShapeAsync(string shape)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("projection-ast", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        OqlProjection[] projections = shape switch
        {
            "multiple" => [new(new OqlLiteralExpression(1m), null), new(new OqlLiteralExpression(2m), null)],
            "alias" => [new(new OqlLiteralExpression(1m), "n")],
            _ => [new(new OqlStarExpression(), null)],
        };
        var request = new DocumentQueryRequest(new OqlQueryStatement(new OqlSelectExpression(
            "items", null, projections, null, [], null, [], false, true)));
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
        error.Message.ShouldBe("SELECT VALUE requires exactly one expression without an alias or star.");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - SELECT constructors wrap deep stored and parameter values within the result limit")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_DeepConstructor_ShouldUseSelectLimitAsync(bool objects)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("projection-depth", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        string stored = new string('[', 128) + "0" + new string(']', 128);
        await PutAsync(collection, session, "a", stored);
        using var parameter = JsonDocument.Parse(new string('[', 150) + "0" + new string(']', 150), new JsonDocumentOptions { MaxDepth = 256 });
        string Wrap(string value) => objects ? "{w: " + value + "}" : "[" + value + "]";
        // Act / Assert
        foreach (string query in new[] { "SELECT " + Wrap("d") + " AS w FROM items d", "SELECT VALUE " + Wrap("d") + " FROM items d" })
        {
            var row = (await RowsAsync(session, query)).ShouldHaveSingleItem();
            using var json = JsonDocument.Parse(row.GetBytes(0), new JsonDocumentOptions { MaxDepth = 256 });
            (objects ? json.RootElement.GetProperty("w") : json.RootElement[0]).GetRawText().ShouldBe(stored);
        }
        var parameters = new Dictionary<string, object?> { ["p"] = parameter.RootElement };
        await using var result = (await session.ExecuteAsync("SELECT VALUE " + Wrap("@p") + " FROM items", parameters, CancellationToken.None)).ShouldBeOfType<DocumentQueryResult>();
        using var writer = new DocumentRowWriter();
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            count++;
            using var json = JsonDocument.Parse(writer.Write(row, result.Columns, result.BareValue), new JsonDocumentOptions { MaxDepth = 256 });
            (objects ? json.RootElement.GetProperty("w") : json.RootElement[0]).GetRawText().ShouldBe(parameter.RootElement.GetRawText());
        }
        count.ShouldBe(1);
        using var limit = JsonDocument.Parse(new string('[', 256) + "0" + new string(']', 256), new JsonDocumentOptions { MaxDepth = 256 });
        parameters["p"] = limit.RootElement;
        string invalid = "SELECT VALUE " + Wrap("@p") + " FROM items";
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(invalid, parameters, CancellationToken.None));
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD05015");
        diagnostic.Start.ShouldBe(13);
        diagnostic.End.ShouldBe(invalid.IndexOf(" FROM", StringComparison.Ordinal));
        error.Message.ShouldBe("SELECT " + (objects ? "object" : "array") + " constructor values must be nested no deeper than 256 levels.");
        parameters["p"] = parameter.RootElement;
        var insertError = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT INTO items (KEY, VALUE) VALUES ('deep', " + Wrap("@p") + ")", parameters, CancellationToken.None));
        insertError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02008");
        insertError.Message.ShouldBe("INSERT JSON must be valid and nested no deeper than 128 levels.");
        var selectInsertError = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT INTO items (KEY k, VALUE v) SELECT 'deep' AS k, " + Wrap("@p") + " AS v FROM items", parameters, CancellationToken.None));
        selectInsertError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02008");
        selectInsertError.Message.ShouldBe("INSERT JSON must be valid and nested no deeper than 128 levels.");
    }
}
