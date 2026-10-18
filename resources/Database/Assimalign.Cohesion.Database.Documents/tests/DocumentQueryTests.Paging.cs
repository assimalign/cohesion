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
    [Fact(DisplayName = "Cohesion Test [Documents] - Paging: grouping, HAVING, DISTINCT and alias ordering precede the page")]
    public async Task Execute_Paging_ShouldFollowRelationalStagesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("paging", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(items, session, "d", "{\"name\":\"delta\",\"g\":\"a\",\"v\":1}");
        await PutAsync(items, session, "c", "{\"name\":\"gamma\",\"g\":\"b\",\"v\":1}");
        await PutAsync(items, session, "b", "{\"name\":\"beta\",\"g\":\"a\",\"v\":1}");
        await PutAsync(items, session, "a", "{\"name\":\"alpha\",\"g\":\"a\",\"v\":1}");

        // Act / Assert
        (await RowsAsync(session, "SELECT name AS n FROM items ORDER BY n LIMIT 1")).Single().GetValue(0).ShouldBe("alpha");
        var group = (await RowsAsync(session, "SELECT g, COUNT(*) AS n FROM items GROUP BY g LIMIT 1")).Single();
        group.GetValue(0).ShouldBe("a");
        group.GetValue(1).ShouldBe(3L);
        (await RowsAsync(session, "SELECT g FROM items GROUP BY g HAVING COUNT(*) > 1 OFFSET 1 LIMIT 1")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT DISTINCT g FROM items OFFSET 1 LIMIT 1")).Single().GetValue(0).ShouldBe("b");
        (await RowsAsync(session, "SELECT DISTINCT g FROM items ORDER BY g DESC LIMIT 1")).Single().GetValue(0).ShouldBe("b");
        (await RowsAsync(session, "SELECT COUNT(*) FROM items LIMIT 0")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT name FROM items OFFSET 100")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT name FROM items LIMIT 9223372036854775807")).Count.ShouldBe(4);
        (await RowsAsync(session, "SELECT name FROM items OFFSET 9223372036854775807 LIMIT 9223372036854775807")).ShouldBeEmpty();
        await session.ExecuteAsync("CREATE INDEX z ON items (g)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX a ON items (v)", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES ORDER BY INDEX_NAME LIMIT 1")).Single().GetValue(0).ShouldBe("a");
        await using var explained = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN SELECT name AS n FROM items ORDER BY n LIMIT @n OFFSET 1",
            new Dictionary<string, object?> { ["n"] = 2 }, CancellationToken.None);
        await foreach (var row in explained.GetRowsAsync(CancellationToken.None))
        {
            if (row.GetInt32(0) == 0) { row.GetString(5).ShouldBe("filter=False; grouped=False; order_keys=1; limit=2; offset=1"); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Paging: tie pages concatenate identically before, during and after index use")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_PagesWithTies_ShouldPreserveIndexParityAsync(bool ordered)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("paging", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        foreach (string id in new[] { "e", "c", "a", "d", "b" })
        {
            await PutAsync(items, session, id, "{\"g\":1}");
        }
        string query = "SELECT META().id AS id FROM items WHERE g = 1" + (ordered ? " ORDER BY g" : "");
        var full = (await RowsAsync(session, query)).Select(row => row.GetString(0)).ToArray();
        var original = new List<string?>();
        // Act / Assert
        for (int phase = 0; phase < 3; phase++)
        {
            var pages = new List<string?>();
            for (int offset = 0; offset < 6; offset += 2)
            {
                pages.AddRange((await RowsAsync(session, query + $" OFFSET {offset} LIMIT 2")).Select(row => row.GetString(0)));
            }
            pages.ShouldBe(full);
            if (phase == 0)
            {
                original.AddRange(pages);
                await session.ExecuteAsync("CREATE INDEX by_g ON items (g)", cancellationToken: CancellationToken.None);
                (await PlanAsync(database, query + " LIMIT 2")).Access.ShouldBeOfType<DocumentIndexPath>();
            }
            else
            {
                pages.ShouldBe(original);
                if (phase == 1) { await session.ExecuteAsync("DROP INDEX by_g ON items", cancellationToken: CancellationToken.None); }
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Paging: metadata and numeric cell promotion are independent of the page")]
    public async Task Execute_Pages_ShouldRetainCompleteColumnTypesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("paging", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(items, session, "a", "{\"v\":1}");
        await PutAsync(items, session, "b", "{\"v\":\"x\"}");

        // Act / Assert
        foreach (string page in new[] { "", " LIMIT 1", " LIMIT 0", " OFFSET 1 LIMIT 1", " OFFSET 100" })
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT v FROM items ORDER BY v" + page, cancellationToken: CancellationToken.None);
            result.Columns.Single().Type.ShouldBe(DatabaseType.Null);
            result.AffectedCount.ShouldBe(-1);
            await using var numeric = (DatabaseResultSet)await session.ExecuteAsync(
                "SELECT CASE WHEN v = 1 THEN LENGTH('a') ELSE 'x' END AS n FROM items" + page, cancellationToken: CancellationToken.None);
            numeric.Columns.Single().Type.ShouldBe(DatabaseType.Null);
            await foreach (var row in numeric.GetRowsAsync(CancellationToken.None))
            {
                if (row.GetValue(0) is not string) { row.GetValue(0).ShouldBeOfType<decimal>().ShouldBe(1m); }
            }
        }
        await using var count = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM items LIMIT 0", cancellationToken: CancellationToken.None);
        count.Columns.Single().Type.ShouldBe(DatabaseType.Int64);
        await using var emptyCount = (DatabaseResultSet)await session.ExecuteAsync(
            "SELECT v, COUNT(*) AS n FROM items WHERE FALSE GROUP BY v LIMIT 0", cancellationToken: CancellationToken.None);
        emptyCount.Columns[1].Type.ShouldBe(DatabaseType.Int64);
        // Lone stars can encounter scalar roots; their column metadata also needs the complete input.
        var roots = await session.CreateCollectionAsync("roots", CancellationToken.None);
        await PutAsync(roots, session, "a", "1");
        await PutAsync(roots, session, "b", "\"x\"");
        await using var star = (DatabaseResultSet)await session.ExecuteAsync("SELECT * FROM roots LIMIT 1", cancellationToken: CancellationToken.None);
        star.Columns.Single().Type.ShouldBe(DatabaseType.Null);
    }

    public static IEnumerable<object?[]> InvalidPagingValues()
    {
        foreach (object? value in new object?[] { -1, 1.5m, "2", null, true, double.NaN, double.PositiveInfinity,
            double.NegativeInfinity, double.Epsilon, -double.Epsilon, float.Epsilon, 1.0000000000000002d, 1.0000001f,
            9223372036854775808d, 9223372036854775808f, decimal.MaxValue, ulong.MaxValue, new object(), default(JsonElement) })
        { yield return [value]; }
        foreach (string json in new[] { "9223372036854775808", "1e999", "1.5", "null", "true", "[1]", "{}", "\"2\"" })
        {
            using var document = JsonDocument.Parse(json);
            yield return [document.RootElement.Clone()];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Paging: all invalid parameters fail planning with the parser count diagnostic")]
    [MemberData(nameof(InvalidPagingValues))]
    public async Task Plan_InvalidPagingParameter_ShouldUseLocatedOql0007Async(object? value)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("paging", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["n"] = value };
        foreach (string source in new[] { "absent", "COHESION_SCHEMA.INDEXES" })
        {
            foreach (string clause in new[] { "LIMIT", "OFFSET" })
            {
                string query = $"SELECT * FROM {source} {clause} @n";
                var parsed = new OqlQueryParser().Parse($"SELECT * FROM c {clause} -1");
                // Act: absent collection proves count validation precedes collection resolution or reads.
                var error = await Should.ThrowAsync<DatabaseException>(async () =>
                    await DocumentPlanTestHelper.PlanAsync(database, query, parameters, CancellationToken.None));
                // Assert
                var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
                diagnostic.Code.ShouldBe("OQL0007");
                diagnostic.Start.ShouldBe(query.Length - 2);
                diagnostic.End.ShouldBe(query.Length);
                diagnostic.Message.ShouldBe(parsed.Diagnostics.Single().Message);
                error.Message.ShouldBe(diagnostic.Message);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Paging: integral CLR and JSON parameters execute")]
    [InlineData(1)]
    [InlineData(1L)]
    [InlineData(2147483648L)]
    [InlineData(long.MaxValue)]
    public async Task Execute_IntegralPagingParameters_ShouldAcceptNormalizedValuesAsync(long value)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("paging", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(items, session, "a", "{}");
        using var json = JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Act / Assert
        foreach (object count in new object[] { value, (decimal)value, json.RootElement, 1, 1d, 1f })
        {
            (await RowsAsync(session, "SELECT * FROM items LIMIT @n", new Dictionary<string, object?> { ["n"] = count })).Count.ShouldBe(1);
        }
        using var integralDecimal = JsonDocument.Parse("1.0");
        (await RowsAsync(session, "SELECT * FROM items LIMIT @n", new Dictionary<string, object?> { ["n"] = integralDecimal.RootElement })).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Paging: input validation, EXPLAIN and direct AST planning cannot bypass counts")]
    public async Task Plan_PagingOnEverySeam_ShouldValidateBeforeReadingAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("paging", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        const string query = "SELECT * FROM items LIMIT @n";
        // Act / Assert
        var missing = await Should.ThrowAsync<DatabaseException>(async () => await PlanAsync(database, query));
        missing.Diagnostics.Single().Code.ShouldBe("COHDOC005");
        missing.Diagnostics.Single().Start.ShouldBe(query.Length - 2);
        missing.Message.ShouldBe("OQL parameter 'n' has no value.");
        var bad = new Dictionary<string, object?> { ["n"] = double.NaN };
        var execute = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, bad, CancellationToken.None));
        execute.Diagnostics.Single().Code.ShouldBe("OQL0007");
        var explain = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("EXPLAIN " + query, bad, CancellationToken.None));
        explain.Diagnostics.Single().Code.ShouldBe("OQL0007");
        var select = new OqlSelectExpression("items", null, [new(new OqlStarExpression(), null)], null, [], null, [], false, false,
            new OqlBinaryExpression(new OqlLiteralExpression(1), "+", new OqlLiteralExpression(1)));
        Should.Throw<DatabaseException>(() => DocumentPlanner.CreateLogicalPlan(select)).Diagnostics.Single().Code.ShouldBe("OQL0007");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Paging: INSERT and UPSERT SELECT consume only the final page")]
    public async Task Execute_WriteSelectPaging_ShouldWriteOnlyPageAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("paging", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION copies", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', 1), ('b', 2), ('c', 3)", cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string verb in new[] { "INSERT", "UPSERT" })
        {
            string statement = $"{verb} INTO copies (KEY id) SELECT META().id AS id FROM items ORDER BY id OFFSET 1 LIMIT 1";
            await using var explain = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN " + statement, cancellationToken: CancellationToken.None);
            bool sourceDescribed = false;
            await foreach (var row in explain.GetRowsAsync(CancellationToken.None))
            {
                if (row.GetString(2) == "Select")
                {
                    row.GetString(5).ShouldBe("filter=False; grouped=False; order_keys=1; limit=1; offset=1");
                    sourceDescribed = true;
                }
            }
            sourceDescribed.ShouldBeTrue();
            var result = await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
            result.AffectedCount.ShouldBe(1);
            (await RowsAsync(session, "SELECT id FROM copies")).Single().GetValue(0).ShouldBe("b");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Paging: fixed-type projections stop unsorted reads after the page")]
    public async Task Execute_UnsortedFixedProjection_ShouldStopBeforeUnusedPredicateAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("paging", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(items, session, "a", "{\"v\":1}");
        await PutAsync(items, session, "b", "{\"v\":0}");
        // Act / Assert: reading the second row would divide by zero.
        (await RowsAsync(session, "SELECT {'v': v} AS obj FROM items WHERE 1 / v > 0 LIMIT 1")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT 1 FROM items WHERE 1 / v > 0 LIMIT 0")).ShouldBeEmpty();
    }
}
