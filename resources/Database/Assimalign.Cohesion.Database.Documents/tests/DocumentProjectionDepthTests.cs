using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentProjectionDepthTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Projection: star depth includes exactly the serialized wrappers")]
    [InlineData(false, false, 255, true)]
    [InlineData(true, false, 255, true)]
    [InlineData(false, true, 254, true)]
    [InlineData(true, true, 254, true)]
    [InlineData(false, true, 255, false)]
    [InlineData(true, true, 255, false)]
    public async Task ExecuteAsync_StarDepth_ShouldFollowRowShapeAsync(bool nested, bool extraColumn, int depth, bool succeeds)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("projection-depth", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        string query = "SELECT *" + (extraColumn ? ", 1 AS w" : "") + " FROM items d UNNEST " +
            (nested ? "(SELECT RAW @deep FROM items LIMIT 1)" : "[@deep]") + " AS v LIMIT 1";
        // Act / Assert
        if (succeeds)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, DocumentSubqueryRegressionTests.DeepParameters(depth), CancellationToken.None);
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { row.GetValue(0).ShouldBeOfType<JsonElement>(); rows++; }
            rows.ShouldBe(1);
        }
        else
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(query, DocumentSubqueryRegressionTests.DeepParameters(depth), CancellationToken.None));
            var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CD05015");
            diagnostic.Start.ShouldBe(7);
            diagnostic.End.ShouldBe(8);
        }
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT n FROM items")).Count.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Projection: developer JSON is checked on both evaluation paths")]
    [InlineData(false, false, 256, false)]
    [InlineData(true, false, 256, false)]
    [InlineData(false, true, 256, true)]
    [InlineData(true, true, 256, true)]
    [InlineData(false, false, 255, true)]
    [InlineData(true, false, 255, true)]
    public async Task ExecuteAsync_DeveloperJson_ShouldValidateCompleteProjectionAsync(bool nested, bool raw, int depth, bool succeeds)
    {
        // Arrange
        var json = (JsonElement)DocumentSubqueryRegressionTests.DeepParameters(depth)["deep"]!;
        var builder = DocumentDatabaseEngine.CreateBuilder("callback-depth");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, DocumentValue>("deep_value", _ => DocumentValue.FromJson(json)));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        string query = "SELECT " + (raw ? "RAW " : "") + "deep_value(1)" + (raw ? "" : " AS v") + " FROM items" +
            (nested ? " WHERE EXISTS (SELECT RAW 1 FROM items)" : "");
        // Act / Assert
        if (succeeds) { (await DocumentSubqueryTests.RowsAsync(session, query)).Count.ShouldBe(3); }
        else
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
            var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CD05015");
            diagnostic.Start.ShouldBe(7);
            diagnostic.End.ShouldBe(20);
        }
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT n FROM items")).Count.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Row writer: depth fallback is a coded statement failure")]
    [InlineData(false, 256)]
    [InlineData(true, 257)]
    public void Write_OverdeepRow_ShouldReturnDepthDiagnostic(bool bare, int depth)
    {
        // Arrange: bypass evaluation to exercise the formatter's last defence.
        using var json = JsonDocument.Parse(new string('[', depth) + "0" + new string(']', depth), new JsonDocumentOptions { MaxDepth = 300 });
        var row = new DocumentQueryResult.DocumentQueryRow([json.RootElement], default, bare);
        DatabaseColumn[] columns = [new() { Name = "v", Ordinal = 0, Type = Assimalign.Cohesion.Database.Types.DatabaseType.Json }];
        using var writer = new DocumentRowWriter();
        // Act
        var location = Location.Create(1, 1, 7, 20);
        var error = Should.Throw<DatabaseException>(() => writer.Write(row, columns, bare, location));
        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD05015");
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(20);
        diagnostic.Line.ShouldBe(1);
        writer.Write(new DocumentQueryResult.DocumentQueryRow([1m], default, bare), columns, bare).IsEmpty.ShouldBeFalse();
    }
}
