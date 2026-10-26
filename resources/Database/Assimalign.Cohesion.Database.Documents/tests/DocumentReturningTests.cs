using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentReturningTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - RETURNING: paged SELECT sources bind only the stored target and EXPLAIN keeps source parents")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    public async Task ExecuteAsync_SelectReturning_ShouldPreserveTargetScopeAndPlanParents(string verb)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.CreateCollectionAsync("archive", CancellationToken.None);
        string statement = verb + " INTO archive AS target (KEY k, VALUE v) " +
            "SELECT META(s).id AS k, {n:s.n} AS v FROM items s ORDER BY n DESC OFFSET 0 LIMIT 1 " +
            "RETURNING META(target).id AS id, target.n AS n, s.n AS absent";

        // Act
        await using var description = (await session.ExecuteAsync("EXPLAIN " + statement, cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        await using var result = (await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await RowsAsync(result);

        // Assert
        result.AffectedCount.ShouldBe(1);
        rows.ShouldHaveSingleItem().GetString(0).ShouldBe("b");
        rows[0].GetValue(1).ShouldBe(2m);
        rows[0].IsMissing(2).ShouldBeTrue();
        var planRows = await RowsAsync(description);
        int sourceId = planRows.Single(row => row.GetString(2) == "Select").GetInt32(0);
        planRows.Single(row => row.GetString(2) == "Project").GetInt32(1).ShouldBe(sourceId);
        planRows.Single(row => row.GetString(2) == "Returning").GetInt32(1).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - RETURNING: contextual field arithmetic and aliases retain their value")]
    public async Task ExecuteAsync_ReturningNamedField_ShouldPreserveExpression()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {returning:7})", cancellationToken: CancellationToken.None);

        // Act
        await using var arithmetic = (await session.ExecuteAsync("DELETE FROM items RETURNING returning + 1 AS x", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        (await RowsAsync(arithmetic)).ShouldHaveSingleItem().GetValue(0).ShouldBe(8m);
        arithmetic.Columns.ShouldHaveSingleItem().Name.ShouldBe("x");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - RETURNING: cancellation observed after a projected image rolls back the writes")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task ExecuteAsync_CancelledProjection_ShouldRollbackWrites(string verb)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("returning");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("cancel_projection", value =>
        {
            if (++calls == 2) { cancellation.Cancel(); }
            return value;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await SeedAsync(session);
        var before = await ImagesAsync(collection, session);

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
            Write(verb) + " RETURNING cancel_projection(n)", cancellationToken: cancellation.Token));

        // Assert
        calls.ShouldBe(2);
        var after = await ImagesAsync(collection, session);
        after.Keys.ShouldBe(before.Keys);
        foreach (var pair in before) { after[pair.Key].Version.ShouldBe(pair.Value.Version); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - RETURNING: deep parameter serialization fails within the mutation transaction")]
    public async Task ExecuteAsync_ReturningDepthExceeded_ShouldRollbackWrites()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await SeedAsync(session);
        var before = await ImagesAsync(collection, session);
        using var deep = JsonDocument.Parse(new string('[', 256) + "0" + new string(']', 256), new JsonDocumentOptions { MaxDepth = 256 });

        // Act: a named projection adds its own object level to the parameter's depth.
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "UPDATE items SET n = 99 RETURNING $deep AS value", new Dictionary<string, object?> { ["deep"] = deep.RootElement }, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05019");
        var after = await ImagesAsync(collection, session);
        foreach (var pair in before) { after[pair.Key].Version.ShouldBe(pair.Value.Version); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - RETURNING: directly constructed empty output names fail before writing")]
    public async Task ExecuteAsync_EmptyReturningAlias_ShouldRejectBeforeMutation()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await SeedAsync(session);
        var returning = new OqlReturningExpression([new OqlProjection(new OqlLiteralExpression(1), "")]);
        var statement = new OqlDeleteExpression("items", null, null, returning: returning);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            new DocumentQueryRequest(new OqlQueryStatement(statement)), CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01026");
        (await ImagesAsync(collection, session)).Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - RETURNING: lone-star raw bytes are checked against the frame limit before deleting")]
    public async Task ExecuteAsync_WholeImageExceedsFrame_ShouldPreserveDocument()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        byte[] content = new byte[Assimalign.Cohesion.Database.Protocol.ProtocolFrameHeader.MaxPayloadLength + 2];
        content.AsSpan().Fill((byte)'x');
        content[0] = content[^1] = (byte)'"';
        var original = await collection.PutAsync(session, "a", content, cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("DELETE FROM items RETURNING *", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05016");
        (await collection.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull().Version.ShouldBe(original.Version);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - RETURNING: owned images and metadata survive session and engine disposal")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task ExecuteAsync_ReturningImages_ShouldOwnRowsAndMutationCount(string verb)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await SeedAsync(session);
        var before = await ImagesAsync(collection, session);
        string write = Write(verb);

        // Act
        await using var result = (await session.ExecuteAsync(write +
            " RETURNING META(d).id AS id, META(d).version AS version, d.n AS n, d.nested AS nested, d.absent AS absent, NULL AS null_value",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var images = verb == "DELETE" ? before : await ImagesAsync(collection, session);
        await session.DisposeAsync();
        await engine.DisposeAsync();
        var rows = await RowsAsync(result);

        // Assert
        result.AffectedCount.ShouldBe(2);
        result.Columns.Select(column => column.Name).ShouldBe(["id", "version", "n", "nested", "absent", "null_value"]);
        result.Columns[1].Type.ShouldBe(DatabaseType.Decimal);
        rows.Count.ShouldBe(2);
        rows.Select(row => row.GetString(0)).ShouldBe(verb == "INSERT" ? ["c", "d"] : ["a", "b"]);
        foreach (var row in rows)
        {
            var image = images[row.GetString(0)!];
            row.GetValue(1).ShouldBe((decimal)image.Version.Value);
            using var json = JsonDocument.Parse(image.Content);
            row.GetValue(2).ShouldBe(json.RootElement.GetProperty("n").GetDecimal());
            JsonElement.DeepEquals(row.GetValue(3).ShouldBeOfType<JsonElement>(), json.RootElement.GetProperty("nested")).ShouldBeTrue();
            row.IsMissing(4).ShouldBeTrue();
            row.IsNull(4).ShouldBeFalse();
            row.IsNull(5).ShouldBeTrue();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - RETURNING: RAW and VALUE normalize MISSING and preserve bare JSON")]
    [InlineData("INSERT", "RAW")]
    [InlineData("UPSERT", "VALUE")]
    [InlineData("UPDATE", "RAW")]
    [InlineData("DELETE", "VALUE")]
    public async Task ExecuteAsync_BareReturning_ShouldUseValueProjectionRules(string verb, string modifier)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        await using var result = (await session.ExecuteAsync(Write(verb) + " RETURNING " + modifier + " d.absent",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await RowsAsync(result);

        // Assert
        result.AffectedCount.ShouldBe(2);
        result.Columns.ShouldHaveSingleItem().Name.ShouldBe("value");
        rows.Count.ShouldBe(2);
        foreach (var row in rows)
        {
            row.IsMissing(0).ShouldBeFalse();
            row.IsNull(0).ShouldBeTrue();
            Encoding.UTF8.GetString(row.GetBytes(0).Span).ShouldBe("null");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - RETURNING: projection failure rolls back every write and index entry")]
    [InlineData("INSERT", false)]
    [InlineData("UPSERT", false)]
    [InlineData("UPDATE", false)]
    [InlineData("DELETE", false)]
    [InlineData("INSERT", true)]
    [InlineData("UPSERT", true)]
    [InlineData("UPDATE", true)]
    [InlineData("DELETE", true)]
    public async Task ExecuteAsync_ProjectionFailure_ShouldRollbackInsideTransaction(string verb, bool explicitTransaction)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX ix ON items (n)", cancellationToken: CancellationToken.None);
        var before = await ImagesAsync(collection, session);
        if (explicitTransaction)
        {
            await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('prior', {n:9})", cancellationToken: CancellationToken.None);
        }
        string statement = Write(verb) + " RETURNING 10 / (d.n - 2) AS quotient";
        if (verb == "UPDATE") { statement = "UPDATE items d SET n = n WHERE n < 3 RETURNING 10 / (d.n - 2) AS quotient"; }
        if (verb == "DELETE") { statement = "DELETE FROM items d WHERE n < 3 RETURNING 10 / (d.n - 2) AS quotient"; }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02006");
        failure.Diagnostics[0].Start.ShouldBe(statement.IndexOf("10 /", StringComparison.Ordinal));
        if (explicitTransaction)
        {
            var refused = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync("SELECT * FROM items", cancellationToken: CancellationToken.None));
            refused.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD04101");
            await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
        }
        var after = await ImagesAsync(collection, session);
        after.Keys.ShouldBe(before.Keys);
        foreach (var pair in before)
        {
            after[pair.Key].Version.ShouldBe(pair.Value.Version);
            after[pair.Key].Content.ToArray().ShouldBe(pair.Value.Content.ToArray());
        }
        await using var indexed = (await session.ExecuteAsync("SELECT n FROM items WHERE n >= 1 ORDER BY n", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        (await RowsAsync(indexed)).Select(row => row.GetValue(0)).ShouldBe([1m, 2m]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - RETURNING: retained rows spend the statement budget and failure rolls back")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task ExecuteAsync_ReturningMemoryExhausted_ShouldRollbackWrites(string verb)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new() { StatementMemoryBudget = 1000 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await SeedAsync(session);
        var before = await ImagesAsync(collection, session);

        // Act: one row fits; retaining the second projected row exceeds the budget.
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            Write(verb) + " RETURNING $payload AS value", new Dictionary<string, object?> { ["payload"] = new string('x', 200) }, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        var after = await ImagesAsync(collection, session);
        after.Keys.ShouldBe(before.Keys);
        foreach (var pair in before) { after[pair.Key].Version.ShouldBe(pair.Value.Version); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - RETURNING: binding and scalar validation precede empty mutation inputs")]
    [InlineData("$missing", "CD01005")]
    [InlineData("COUNT(*)", "CD05001")]
    [InlineData("META(other).id", "CD05001")]
    [InlineData("n AS repeated, n AS repeated", "CD01003")]
    public async Task ExecuteAsync_EmptyReturningInvalid_ShouldValidateBeforeMutation(string projection, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("DELETE FROM items d RETURNING " + projection, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - RETURNING: lone star preserves DELETE pre-image bytes and empty results retain metadata")]
    public async Task ExecuteAsync_StarAndEmptyReturning_ShouldPreserveShape()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("returning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        const string bytes = " \r\n{ \"n\" : 1, \"nested\" : [true,null] }\t";
        await collection.PutAsync(session, "a", Encoding.UTF8.GetBytes(bytes), cancellationToken: CancellationToken.None);

        // Act
        await using var deleted = (await session.ExecuteAsync("DELETE FROM items RETURNING *", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        await using var empty = (await session.ExecuteAsync("UPDATE items SET n = 1 RETURNING LENGTH('x') AS length", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        deleted.AffectedCount.ShouldBe(1);
        Encoding.UTF8.GetString((await RowsAsync(deleted)).ShouldHaveSingleItem().GetBytes(0).Span).ShouldBe(bytes);
        empty.AffectedCount.ShouldBe(0);
        empty.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Int64);
        (await RowsAsync(empty)).ShouldBeEmpty();
        var options = new DocumentDatabaseEngineOptions { StatementMemoryBudget = 0 };
        Should.Throw<ArgumentOutOfRangeException>(() => DocumentDatabaseEngine.Create("invalid", options)).ParamName.ShouldBe("StatementMemoryBudget");
    }

    internal static string Write(string verb) => verb switch
    {
        "INSERT" => "INSERT INTO items AS d (KEY, VALUE) VALUES ('c', {n:1,nested:[1]}), ('d', {n:2,nested:[2]})",
        "UPSERT" => "UPSERT INTO items AS d (KEY, VALUE) VALUES ('a', {n:1,nested:[3]}), ('b', {n:2,nested:[4]})",
        "UPDATE" => "UPDATE items d SET n = n + 10",
        _ => "DELETE FROM items d",
    };

    private static async Task<DocumentCollection> SeedAsync(DocumentDatabaseSession session)
    {
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1,nested:[1]}), ('b', {n:2,nested:[2]})", cancellationToken: CancellationToken.None);
        return collection;
    }

    private static async Task<Dictionary<string, Document>> ImagesAsync(DocumentCollection collection, DocumentDatabaseSession session)
    {
        var result = new Dictionary<string, Document>(StringComparer.Ordinal);
        foreach (string id in new[] { "a", "b", "c", "d", "prior" })
        {
            if (await collection.GetAsync(session, new DocumentId(id), CancellationToken.None) is { } document) { result.Add(id, document); }
        }
        return result;
    }

    private static async Task<List<DatabaseRow>> RowsAsync(DatabaseResultSet result)
    {
        var rows = new List<DatabaseRow>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
        return rows;
    }
}
