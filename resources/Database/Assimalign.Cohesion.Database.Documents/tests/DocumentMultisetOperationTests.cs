using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentMultisetOperationTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Multisets: structural equality includes NULL MISSING and numeric equivalence")]
    [InlineData("SELECT RAW MISSING FROM [1,2] AS a INTERSECT ALL SELECT RAW NULL FROM [1] AS b", "null")]
    [InlineData("SELECT RAW MISSING FROM [1,2] AS a EXCEPT ALL SELECT RAW NULL FROM [1] AS b", "null")]
    [InlineData("SELECT MISSING AS a FROM [1] AS x INTERSECT SELECT MISSING AS b FROM [1] AS y", "{}")]
    [InlineData("SELECT MISSING AS a FROM [1] AS x EXCEPT SELECT MISSING AS b FROM [1] AS y", "")]
    [InlineData("SELECT MISSING AS a FROM [1] AS x INTERSECT SELECT NULL AS a FROM [1] AS y", "")]
    [InlineData("SELECT MISSING AS a FROM [1] AS x EXCEPT SELECT NULL AS a FROM [1] AS y", "<missing>")]
    [InlineData("SELECT RAW x FROM [1.00,2,1.0,3,2] AS x INTERSECT SELECT RAW y FROM [1,2.0,2] AS y", "1.00|2")]
    [InlineData("SELECT RAW x FROM [1.00,2,1.0,3,2] AS x INTERSECT ALL SELECT RAW y FROM [1,2.0,2] AS y", "1.00|2|2")]
    [InlineData("SELECT RAW x FROM [1.00,2,1.0,3,2] AS x EXCEPT SELECT RAW y FROM [2.0] AS y", "1.00|3")]
    [InlineData("SELECT RAW x FROM [1.00,2,1.0,3,2] AS x EXCEPT ALL SELECT RAW y FROM [1,2.0] AS y", "1.0|3|2")]
    [InlineData("SELECT RAW {b:2,a:1.00} FROM [1,2] AS x INTERSECT SELECT RAW {a:1,b:2.0} FROM [1] AS y", "{\"b\":2,\"a\":1.00}")]
    [InlineData("SELECT 1 AS a FROM [1] AS x INTERSECT SELECT 1 AS b FROM [1] AS y", "")]
    [InlineData("SELECT 1 AS a FROM [1] AS x EXCEPT SELECT 1 AS b,2 AS c FROM [1] AS y", "{\"a\":1}")]
    [InlineData("SELECT RAW 1 FROM [1] AS x UNION SELECT RAW 2 FROM [1] AS y INTERSECT SELECT RAW 2 FROM [1] AS z", "2")]
    [InlineData("SELECT RAW 1 FROM [1] AS x EXCEPT SELECT RAW 1 FROM [1] AS y UNION ALL SELECT RAW 2 FROM [1] AS z", "2")]
    public async Task ExecuteAsync_EqualityMatrix_ShouldKeepLeftValuesAsync(string text, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("multiset-equality", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        string.Join("|", await ValuesAsync(session, text)).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Compounds: subquery derived correlation and IN positions preserve query values")]
    [InlineData("SELECT RAW (SELECT RAW x FROM [1,1,2] AS x INTERSECT ALL SELECT RAW y FROM [1,2,2] AS y) FROM [0] AS seed", "[1,2]")]
    [InlineData("SELECT RAW (SELECT MISSING AS a FROM [0] AS x UNION SELECT NULL AS b FROM [0] AS y) FROM [0] AS seed", "[{}, {\"b\":null}]")]
    [InlineData("SELECT RAW q FROM (SELECT 1 AS a FROM [0] AS x EXCEPT SELECT 1 AS b FROM [0] AS y) AS q", "{\"a\":1}")]
    [InlineData("SELECT RAW q FROM (SELECT RAW 1 FROM [0] AS x UNION ALL SELECT 2 AS b FROM [0] AS y) AS q", "1|{\"b\":2}")]
    [InlineData("SELECT RAW q FROM ((SELECT RAW x FROM [3,2,1] AS x LIMIT 2) EXCEPT ALL SELECT RAW 2 FROM [0] AS y) AS q", "3")]
    [InlineData("SELECT RAW x FROM [1,2,3] AS x WHERE x IN ((SELECT RAW 1 FROM [0] AS y) UNION SELECT RAW 3 FROM [0] AS z)", "1|3")]
    [InlineData("SELECT RAW x FROM [1,2,3] AS x WHERE x NOT IN ((SELECT RAW 1 FROM [0] AS y) EXCEPT SELECT RAW 2 FROM [0] AS z)", "2|3")]
    [InlineData("SELECT RAW x FROM [1,2] AS x WHERE x IN ((SELECT RAW 1 FROM [0] AS y))", "")]
    [InlineData("SELECT RAW (SELECT RAW o.n FROM [0] AS x INTERSECT SELECT RAW y FROM [1,2] AS y) FROM [{n:1},{n:3}] AS o", "[1]|[]")]
    [InlineData("SELECT RAW (SELECT RAW i.n FROM o AS i UNION ALL SELECT RAW o.n FROM [0] AS x) FROM [{n:1},{n:2}] AS o", "[1,1]|[2,2]")]
    [InlineData("SELECT RAW (SELECT RAW x FROM o.ns AS x EXCEPT ALL SELECT RAW o.n FROM [0] AS y) FROM [{ns:[1,2,1],n:1},{ns:[3,4],n:4}] AS o", "[2,1]|[3]")]
    [InlineData("SELECT RAW (SELECT RAW q.n FROM (SELECT o.n AS n FROM [0] AS x INTERSECT SELECT o.n AS n FROM [0] AS y) AS q) FROM [{n:1},{n:2}] AS o", "[1]|[2]")]
    [InlineData("SELECT RAW (SELECT o.n AS n FROM [0] AS x UNION SELECT o.n AS n FROM [0] AS y) FROM [{n:1},{n:1},{n:2}] AS o GROUP BY o.n", "[{\"n\":1}]|[{\"n\":2}]")]
    [InlineData("SELECT RAW (SELECT RAW o.n FROM seed AS s INTERSECT SELECT RAW o.n FROM seed AS t) FROM [{n:1},{n:2}] AS o", "[1]|[2]")]
    [InlineData("SELECT RAW (SELECT RAW o.n FROM [0] AS x UNION SELECT RAW o.n FROM [0] AS y) FROM [{n:1},{n:2}] AS o GROUP BY (SELECT RAW o.n FROM [0] AS x UNION SELECT RAW o.n FROM [0] AS y)", "[1]|[2]")]
    [InlineData("SELECT RAW (SELECT x AS n FROM [2,1] AS x UNION ALL SELECT y AS n FROM [] AS y ORDER BY n) FROM [0] AS n", "[{\"n\":1},{\"n\":2}]")]
    [InlineData("SELECT RAW (SELECT x AS n FROM [2,1] AS x INTERSECT SELECT y AS n FROM [1,2] AS y ORDER BY n) FROM [0] AS n", "[{\"n\":1},{\"n\":2}]")]
    [InlineData("SELECT RAW (SELECT x AS n FROM [1,2] AS x EXCEPT ALL SELECT y AS m FROM [] AS y ORDER BY m - n) FROM [0] AS m", "[{\"n\":1},{\"n\":2}]")]
    [InlineData("SELECT RAW EXISTS (SELECT RAW x FROM [1,2] AS x EXCEPT SELECT RAW 1 FROM [0] AS y) FROM [0] AS seed", "True")]
    public async Task ExecuteAsync_QueryPositions_ShouldPreserveValuesAsync(string text, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("multiset-positions", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("seed", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO seed (KEY,VALUE) VALUES ('a',{n:1})", cancellationToken: CancellationToken.None);
        // Act / Assert: JSON whitespace is not part of the value contract.
        string.Join("|", await ValuesAsync(session, text)).ShouldBe(expected.Replace("{}, {", "{},{", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Compounds: INSERT and UPSERT bind typed and heterogeneous source members")]
    [InlineData("INSERT", "UNION ALL", false)]
    [InlineData("UPSERT", "UNION ALL", false)]
    [InlineData("INSERT", "INTERSECT ALL", false)]
    [InlineData("UPSERT", "EXCEPT ALL", false)]
    [InlineData("INSERT", "UNION ALL", true)]
    [InlineData("UPSERT", "UNION ALL", true)]
    [InlineData("INSERT", "EXCEPT", true)]
    [InlineData("UPSERT", "INTERSECT", true)]
    public async Task ExecuteAsync_WriteSources_ShouldBindMembersAndReturnImagesAsync(string verb, string operation, bool documentShape)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("multiset-writes", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("dest", CancellationToken.None);
        string left = "SELECT 'a' AS k,{n:1} AS v FROM [0] AS x";
        string right = operation.StartsWith("UNION", StringComparison.Ordinal)
            ? "SELECT " + (documentShape ? "{n:2} AS v,'b' AS k" : "'b' AS k,{n:2} AS v") + " FROM [0] AS y"
            : "SELECT " + (documentShape ? "{n:1} AS v,'a' AS k" : "'a' AS k,{n:1} AS v") + " FROM [0] AS y";
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(verb + " INTO dest (KEY k, VALUE v) " + left + " " + operation + " " + right + " RETURNING RAW dest.n", cancellationToken: CancellationToken.None);
        var actual = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { actual.Add(row.GetString(0)!); }
        // Assert
        actual.ShouldBe(operation.StartsWith("UNION", StringComparison.Ordinal) ? new[] { "1", "2" } : operation.StartsWith("INTERSECT", StringComparison.Ordinal) ? new[] { "1" } : Array.Empty<string>());
        (await ValuesAsync(session, "SELECT RAW COUNT(*) FROM dest")).Single().ShouldBe(actual.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Multisets: zero limits open neither operand in every query position")]
    [InlineData("SELECT RAW x FROM [1/0] AS x INTERSECT SELECT RAW y FROM [1/0] AS y LIMIT 0")]
    [InlineData("SELECT RAW x FROM [1/0] AS x EXCEPT ALL SELECT RAW y FROM [1/0] AS y LIMIT 0")]
    [InlineData("SELECT RAW q FROM (SELECT x AS n FROM [1/0] AS x INTERSECT SELECT y AS n FROM [1/0] AS y ORDER BY n LIMIT 0) AS q")]
    [InlineData("SELECT RAW (SELECT RAW x FROM [1/0] AS x EXCEPT SELECT RAW y FROM [1/0] AS y LIMIT 0) FROM [] AS seed")]
    public async Task ExecuteAsync_ZeroLimit_ShouldOpenNoSourceAsync(string text)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("multiset-zero", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await ValuesAsync(session, text)).ShouldBeEmpty();
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Multisets: right equality state refuses at the statement budget and releases every reservation")]
    [InlineData("INTERSECT")]
    [InlineData("INTERSECT ALL")]
    [InlineData("EXCEPT")]
    [InlineData("EXCEPT ALL")]
    public async Task ExecuteAsync_RetainedRightValues_ShouldChargeAndReleaseAsync(string operationName)
    {
        // Arrange: no output and a plain collection operand isolate right-side retention.
        var builder = DocumentDatabaseEngine.CreateBuilder("multiset-memory");
        builder.Options.StatementMemoryBudget = 64 * 1024;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int index = 0; index < 80; index++)
        { await items.PutAsync(session, index.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Text.Encoding.UTF8.GetBytes("{\"v\":\"" + index + new string('x', 1024) + "\"}"), cancellationToken: CancellationToken.None); }
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation,
                DocumentQueryRequest.FromOql("SELECT RAW 1 FROM [] AS x " + operationName + " SELECT RAW v FROM items").Statement, null, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
        (await ValuesAsync(session, "SELECT RAW 1 FROM [0] AS x " + operationName + " SELECT RAW 1 FROM [0] AS y")).Count.ShouldBe(operationName.StartsWith("INTERSECT", StringComparison.Ordinal) ? 1 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Compounds: EXPLAIN snapshots contain every nested and DML operand without opening sources")]
    [InlineData("SELECT RAW q FROM (SELECT 1 AS n FROM [1/0] AS x INTERSECT ALL SELECT 1 AS n FROM [1/0] AS y EXCEPT SELECT 2 AS n FROM [1/0] AS z) AS q", "Select|ProjectValue|DerivedSource|Except|IntersectAll|Select|Project|ExpressionSource|Select|Project|ExpressionSource|Select|Project|ExpressionSource")]
    [InlineData("INSERT INTO dest (KEY k) SELECT 'a' AS k FROM [1/0] AS x EXCEPT ALL SELECT 'b' AS k FROM [1/0] AS y", "InsertSelect|ExceptAll|Select|Project|ExpressionSource|Select|Project|ExpressionSource")]
    [InlineData("SELECT RAW (SELECT RAW 1 FROM [1/0] AS x INTERSECT SELECT RAW 1 FROM [1/0] AS y) FROM [1/0] AS seed", "Select|ProjectValue|ExpressionSource|SubqueryExpression|Intersect|Select|ProjectValue|ExpressionSource|Select|ProjectValue|ExpressionSource")]
    public async Task ExecuteAsync_ExplainPositions_ShouldDescribeCompleteTreeAsync(string text, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("multiset-explain", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("dest", CancellationToken.None);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN " + text, cancellationToken: CancellationToken.None);
        var operators = new List<string?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { operators.Add(row.GetString(2)); }
        // Assert
        string.Join("|", operators).ShouldBe(expected);
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Compounds: grouping keeps each operand's capture identity")]
    [InlineData("SELECT RAW (SELECT RAW o.n FROM [0] AS x INTERSECT SELECT RAW o.m FROM [0] AS y) FROM [{n:1,m:2}] AS o GROUP BY o.n")]
    [InlineData("SELECT RAW (SELECT RAW o.n FROM [0] AS x UNION SELECT RAW o.n FROM [0] AS y) FROM [{n:1}] AS o GROUP BY (SELECT RAW o.n FROM [0] AS x INTERSECT SELECT RAW o.n FROM [0] AS y)")]
    public async Task ExecuteAsync_UngroupedCapture_ShouldRefuseAsync(string text)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("multiset-group-capture", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Multisets: repeated right values retain one key while their occurrence counter grows")]
    [InlineData("INTERSECT ALL")]
    [InlineData("EXCEPT ALL")]
    public async Task ExecuteAsync_RepeatedRightValue_ShouldRetainOneEqualityKeyAsync(string operationName)
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("multiset-duplicate-memory");
        builder.Options.StatementMemoryBudget = 64 * 1024;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        byte[] content = System.Text.Encoding.UTF8.GetBytes("{\"v\":\"" + new string('x', 1024) + "\"}");
        for (int index = 0; index < 80; index++)
        { await items.PutAsync(session, index.ToString(System.Globalization.CultureInfo.InvariantCulture), content, cancellationToken: CancellationToken.None); }
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation,
                DocumentQueryRequest.FromOql("SELECT RAW 1 FROM [] AS x " + operationName + " SELECT RAW v FROM items").Statement, null, CancellationToken.None))
            {
                int count = 0;
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
                count.ShouldBe(0);
            }
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Compounds: omitted DML VALUE uses the complete RAW row")]
    public async Task ExecuteAsync_RawWriteSource_ShouldStoreCompleteValueAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("multiset-raw-write", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("dest", CancellationToken.None);
        // Act
        var result = await session.ExecuteAsync("INSERT INTO dest (KEY \"value\") SELECT RAW 'a' FROM [0] AS x UNION SELECT RAW 'b' FROM [0] AS y", cancellationToken: CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(2);
        (await ValuesAsync(session, "SELECT RAW dest FROM dest")).ShouldBe(new[] { "a", "b" });
    }

    private static async Task<List<string>> ValuesAsync(DocumentDatabaseSession session, string text)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(text, cancellationToken: CancellationToken.None);
        var values = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { values.Add(row.IsMissing(0) ? "<missing>" : row.IsNull(0) ? "null" : row.GetString(0)!); }
        return values;
    }
}
