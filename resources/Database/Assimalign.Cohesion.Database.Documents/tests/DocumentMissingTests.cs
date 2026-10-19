using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentMissingTests
{
    public static IEnumerable<object[]> BooleanCases()
    {
        string[] operands = ["TRUE", "FALSE", "NULL", "MISSING"];
        string[,] and = { { "TRUE", "FALSE", "NULL", "MISSING" }, { "FALSE", "FALSE", "FALSE", "FALSE" },
            { "NULL", "FALSE", "NULL", "MISSING" }, { "MISSING", "FALSE", "MISSING", "MISSING" } };
        string[,] or = { { "TRUE", "TRUE", "TRUE", "TRUE" }, { "TRUE", "FALSE", "NULL", "MISSING" },
            { "TRUE", "NULL", "NULL", "NULL" }, { "TRUE", "MISSING", "NULL", "MISSING" } };
        string[] not = ["FALSE", "TRUE", "NULL", "MISSING"];
        for (int i = 0; i < operands.Length; i++)
        {
            for (int j = 0; j < operands.Length; j++) { yield return [operands[i], operands[j], and[i, j], or[i, j], not[i]]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - All four-valued Boolean combinations follow Couchbase")]
    [MemberData(nameof(BooleanCases))]
    public async Task Execute_BooleanValues_ShouldFollowCouchbaseAsync(string left, string right, string and, string or, string not)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("missing", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "one", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, $"SELECT {left} AND {right} AS a, {left} OR {right} AS o, NOT {left} AS n FROM items");

        // Assert
        Check(rows.Single(), 0, and);
        Check(rows.Single(), 1, or);
        Check(rows.Single(), 2, not);
        (await RowsAsync(session, $"SELECT COUNT(*) FROM items WHERE {left} AND {right}"))
            .Single().GetInt32(0).ShouldBe(and == "TRUE" ? 1 : 0);
        (await RowsAsync(session, $"SELECT COUNT(*) FROM items HAVING {left} OR {right}"))
            .Count.ShouldBe(or == "TRUE" ? 1 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Arithmetic, comparison and unary operations propagate MISSING before NULL")]
    [InlineData("MISSING = 1")]
    [InlineData("NULL = MISSING")]
    [InlineData("MISSING != MISSING")]
    [InlineData("MISSING < 1")]
    [InlineData("MISSING + 1")]
    [InlineData("MISSING + NULL")]
    [InlineData("NULL + MISSING")]
    [InlineData("MISSING - 1")]
    [InlineData("MISSING * 1")]
    [InlineData("MISSING / 0")]
    [InlineData("MISSING % 0")]
    [InlineData("+MISSING")]
    [InlineData("-MISSING")]
    [InlineData("NOT MISSING")]
    [InlineData("MISSING IS NULL")]
    [InlineData("MISSING IS NOT NULL")]
    public async Task Execute_MissingOperand_ShouldRemainMissingAsync(string expression)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("missing", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "one", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);

        // Act
        var row = (await RowsAsync(session, $"SELECT {expression} FROM items")).Single();

        // Assert
        Check(row, 0, "MISSING");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Presence tests distinguish ordinary, NULL and MISSING values")]
    [InlineData("1", false, true, false, true, true, false)]
    [InlineData("NULL", true, false, false, true, false, true)]
    [InlineData("MISSING", null, null, true, false, false, true)]
    public async Task Execute_PresenceTests_ShouldDistinguishStatesAsync(string value, bool? isNull, bool? notNull,
        bool missing, bool notMissing, bool valued, bool notValued)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("missing", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "one", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);

        // Act
        var row = (await RowsAsync(session, $"SELECT {value} IS NULL AS n, {value} IS NOT NULL AS nn, {value} IS MISSING AS m, {value} IS NOT MISSING AS nm, {value} IS VALUED AS v, {value} IS NOT VALUED AS nv FROM items")).Single();

        // Assert
        Check(row, 0, isNull?.ToString().ToUpperInvariant() ?? "MISSING");
        Check(row, 1, notNull?.ToString().ToUpperInvariant() ?? "MISSING");
        Enumerable.Range(2, 4).Select(row.GetBoolean).ShouldBe([missing, notMissing, valued, notValued]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Navigation through absent, null, scalar and incompatible shapes yields MISSING")]
    [InlineData("{}")]
    [InlineData("{\"v\":null}")]
    [InlineData("{\"v\":1}")]
    [InlineData("{\"v\":[]}")]
    [InlineData("{\"v\":{}}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    public async Task Execute_InvalidNavigation_ShouldReturnMissingAsync(string json)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("missing", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "one", Encoding.UTF8.GetBytes(json), cancellationToken: CancellationToken.None);

        // Act
        var row = (await RowsAsync(session, "SELECT d.v.x AS x, d.v[0] AS z, d.v[99] AS beyond FROM items AS d")).Single();

        // Assert
        for (int ordinal = 0; ordinal < 3; ordinal++) { Check(row, ordinal, "MISSING"); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Aggregates skip absent values and grouping and ordering retain separate MISSING and NULL keys")]
    public async Task Execute_GroupingOrderingAndTypes_ShouldKeepMissingSeparateAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("missing", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        string[] documents = ["{}", "{}", "{\"v\":null}", "{\"v\":false}", "{\"v\":true}", "{\"v\":2}", "{\"v\":4}", "{\"v\":\"text\"}", "{\"v\":[]}", "{\"v\":{}}"];
        for (int i = 0; i < documents.Length; i++)
        {
            string content = documents[i] == "{}" ? $"{{\"id\":{i}}}" : documents[i].Insert(1, $"\"id\":{i},");
            await collection.PutAsync(session, i.ToString("D2"), Encoding.UTF8.GetBytes(content), cancellationToken: CancellationToken.None);
        }

        // Act
        var ascending = await RowsAsync(session, "SELECT v FROM items ORDER BY v");
        var descending = await RowsAsync(session, "SELECT v FROM items ORDER BY v DESC");
        var groups = await RowsAsync(session, "SELECT v, COUNT(*) AS total FROM items GROUP BY v");
        var aggregates = await RowsAsync(session, "SELECT COUNT(*) AS total, COUNT(v) AS present, SUM(v) AS sum, AVG(v) AS average, MIN(v) AS minimum, MAX(v) AS maximum FROM items WHERE v IS NOT VALUED OR v = 2 OR v = 4");

        // Assert
        ascending.Count.ShouldBe(10);
        Check(ascending[0], 0, "MISSING");
        Check(ascending[1], 0, "MISSING");
        Check(ascending[2], 0, "NULL");
        ascending[3].GetBoolean(0).ShouldBeFalse();
        ascending[4].GetBoolean(0).ShouldBeTrue();
        ascending[5].GetValue(0).ShouldBe(2m);
        ascending[6].GetValue(0).ShouldBe(4m);
        ascending[7].GetValue(0).ShouldBe("text");
        ascending[8].GetValue(0).ShouldBeOfType<JsonElement>().ValueKind.ShouldBe(JsonValueKind.Array);
        ascending[9].GetValue(0).ShouldBeOfType<JsonElement>().ValueKind.ShouldBe(JsonValueKind.Object);
        descending[7].IsNull(0).ShouldBeTrue();
        descending[8].IsMissing(0).ShouldBeTrue();
        groups.Count.ShouldBe(9);
        Check(groups[0], 0, "MISSING");
        groups[0].GetInt32(1).ShouldBe(2);
        Check(groups[1], 0, "NULL");
        Enumerable.Range(0, 6).Select(aggregates.Single().GetValue).ShouldBe([5L, 2L, 6m, 3m, 2m, 4m]);
        var constants = (await RowsAsync(session, "SELECT MISSING AS m, COUNT(*) AS total FROM items GROUP BY MISSING")).Single();
        constants.IsMissing(0).ShouldBeTrue();
        constants.GetInt32(1).ShouldBe(10);
        await using var typed = (DatabaseResultSet)await session.ExecuteAsync("SELECT v FROM items WHERE v IS MISSING OR v = 2", cancellationToken: CancellationToken.None);
        typed.Columns.Single().Type.ShouldBe(DatabaseType.Decimal);
        // Identity ties retain the first two source identities in either direction.
        var ties = await RowsAsync(session, "SELECT id, MISSING AS m FROM items ORDER BY m DESC");
        ties.Select(row => row.GetInt32(0)).ShouldBe(Enumerable.Range(0, documents.Length));
    }

    private static void Check(DatabaseRow row, int ordinal, string expected)
    {
        row.SupportsMissing.ShouldBeTrue();
        row.IsMissing(ordinal).ShouldBe(expected == "MISSING");
        row.IsNull(ordinal).ShouldBe(expected == "NULL");
        row.GetValue(ordinal).ShouldBe(expected is "MISSING" or "NULL" ? null : expected == "TRUE");
    }

    private static async Task<List<DatabaseRow>> RowsAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var rows = new List<DatabaseRow>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
        return rows;
    }
}
