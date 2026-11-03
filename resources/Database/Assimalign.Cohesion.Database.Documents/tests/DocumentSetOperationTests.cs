using System;
using System.Collections.Generic;
using System.Globalization;
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
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentSetOperationTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - UNION: a generated concatenation and distinct oracle preserves first representatives")]
    [InlineData(false, "raw")]
    [InlineData(true, "raw")]
    [InlineData(false, "document")]
    [InlineData(true, "document")]
    [InlineData(false, "columns")]
    [InlineData(true, "columns")]
    public async Task ExecuteAsync_GeneratedValues_ShouldMatchOracleAsync(bool all, string mode)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-oracle", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        bool named = mode != "raw";
        var random = new Random(1803);
        string[] palette = ["null", "true", "false", "1", "1.0", "2", "-1", "1.25", "\"a\"", "[]", "[1,null]", "[1.0,null]", "{}", "{\"a\":1,\"b\":2}", "{\"b\":2.0,\"a\":1.0}"];
        if (named) { palette = ["{}", "{\"a\":null}", "{\"b\":null}", "{\"a\":1}", "{\"a\":1.0}", "{\"a\":1,\"b\":2}", "{\"b\":2.0,\"a\":1.0}", "{\"a\":[1,null],\"b\":{\"x\":2}}", "{\"b\":{\"x\":2.0},\"a\":[1.0,null]}"]; }
        if (mode == "columns") { palette = ["{\"a\":{\"x\":1,\"y\":2},\"b\":1}", "{\"a\":{\"y\":2.0,\"x\":1.0},\"b\":1.0}", "{\"a\":[1,null],\"b\":-1}", "{\"a\":[1.0,null],\"b\":-1.0}", "{\"a\":{},\"b\":1.25}"]; }
        for (int trial = 0; trial < 64; trial++)
        {
            string[] left = Enumerable.Range(0, random.Next(25)).Select(_ => palette[random.Next(palette.Length)]).ToArray();
            string[] right = Enumerable.Range(0, random.Next(25)).Select(_ => palette[random.Next(palette.Length)]).ToArray();
            // Pin a left representative, duplicates within/across inputs, then a right representative.
            if (mode == "columns" && trial == 0)
            {
                left = ["{\"a\":{\"z\":1,\"a\":2},\"b\":3.00}", "{\"a\":{\"a\":2.0,\"z\":1.0},\"b\":3}"];
                right = [left[1], "{\"a\":{\"r\":4,\"q\":5},\"b\":4.50}", "{\"a\":{\"q\":5.0,\"r\":4.0},\"b\":4.5}"];
            }
            using var a = JsonDocument.Parse("[" + string.Join(",", left) + "]");
            using var b = JsonDocument.Parse("[" + string.Join(",", right) + "]");
            using var projected = JsonDocument.Parse("[" + string.Join(",", a.RootElement.EnumerateArray().Select(value => Project(value, named, false))
                .Concat(b.RootElement.EnumerateArray().Select(value => Project(value, named, mode == "document")))) + "]");
            IEnumerable<JsonElement> expected = projected.RootElement.EnumerateArray();
            if (!all) { expected = expected.DistinctBy(Canonical, StringComparer.Ordinal); }
            // Act
            string text = named ? "SELECT a.a AS a,a.b AS b FROM @a AS a UNION " + (all ? "ALL " : "") + (mode == "columns" ? "SELECT b.a AS a,b.b AS b FROM @b AS b" : "SELECT b.b AS b,b.a AS a FROM @b AS b")
                : "SELECT RAW a FROM @a AS a UNION " + (all ? "ALL " : "") + "SELECT RAW b FROM @b AS b";
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(text,
                new Dictionary<string, object?> { ["a"] = a.RootElement, ["b"] = b.RootElement }, CancellationToken.None);
            var actual = new List<string>();
            var cells = new List<(string Json, decimal Number)>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                if (mode == "columns")
                { cells.Add((row.GetValue(0).ShouldBeOfType<JsonElement>().GetRawText(), Convert.ToDecimal(row.GetValue(1), CultureInfo.InvariantCulture))); }
                else { using var value = JsonDocument.Parse(row.GetBytes(0)); actual.Add(value.RootElement.GetRawText()); }
            }
            // Assert
            if (mode == "columns")
            {
                result.Columns.Select(column => column.Name).ShouldBe(new[] { "a", "b" });
                result.Columns.Select(column => column.Type).ShouldBe(expected.Any()
                    ? new[] { DatabaseType.Json, DatabaseType.Decimal } : new[] { DatabaseType.Null, DatabaseType.Null });
                cells.ShouldBe(expected.Select(value => (value.GetProperty("a").GetRawText(), value.GetProperty("b").GetDecimal())));
                if (trial == 0 && !all)
                { cells.ShouldBe(new[] { ("{\"z\":1,\"a\":2}", 3m), ("{\"r\":4,\"q\":5}", 4.5m) }); }
            }
            else
            {
                result.Columns.Single().Name.ShouldBe(named ? "document" : "value");
                actual.ToArray().ShouldBe(expected.Select(value => value.GetRawText()).ToArray());
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNION: NULL MISSING and structural numeric equality use row values")]
    [InlineData("SELECT RAW MISSING FROM [1] AS a UNION SELECT VALUE NULL FROM [1] AS b", 1)]
    [InlineData("SELECT RAW MISSING FROM [1] AS a UNION ALL SELECT VALUE NULL FROM [1] AS b", 2)]
    [InlineData("SELECT MISSING AS a FROM [1] AS x UNION SELECT MISSING AS b FROM [1] AS y", 1)]
    [InlineData("SELECT MISSING AS a FROM [1] AS x UNION SELECT NULL AS a FROM [1] AS y", 2)]
    [InlineData("SELECT 1 AS a FROM [1] AS x UNION SELECT 1.0 AS a FROM [1] AS y", 1)]
    [InlineData("SELECT RAW {a:1,b:[2]} FROM [1] AS x UNION SELECT b,a FROM [{a:1.0,b:[2.0]}] AS y", 1)]
    [InlineData("SELECT 1 AS a FROM [1] AS x UNION SELECT 1 AS b FROM [1] AS y", 2)]
    [InlineData("SELECT RAW x FROM [2] AS x WHERE x IN ((SELECT RAW 2 FROM [1] AS seed))", 1)]
    [InlineData("SELECT RAW x FROM [2,3] AS x WHERE x NOT IN ((SELECT RAW 2 FROM [1] AS seed))", 1)]
    public async Task ExecuteAsync_EqualityMatrix_ShouldUseValueSemanticsAsync(string text, int expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-equality", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(text, cancellationToken: CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
        // Assert
        count.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNION: T3-30 chooses static value typed-column and document headers")]
    [InlineData("SELECT RAW 1 FROM [1] AS a UNION SELECT VALUE 2 FROM [1] AS b", "value", DatabaseType.Decimal)]
    [InlineData("SELECT LENGTH('a') AS n FROM [1] AS a UNION ALL SELECT 2 AS n FROM [1] AS b", "n", DatabaseType.Decimal)]
    [InlineData("SELECT 1 AS a FROM [1] AS a UNION ALL SELECT 2 AS b FROM [1] AS b", "document", DatabaseType.Json)]
    [InlineData("SELECT RAW 1 FROM [1] AS a UNION ALL SELECT 2 AS a FROM [1] AS b", "document", DatabaseType.Json)]
    [InlineData("SELECT 1 AS a,2 AS b FROM [1] AS x UNION ALL SELECT 3 AS c FROM [1] AS y", "document", DatabaseType.Json)]
    [InlineData("SELECT 1 AS a, 2 AS b FROM [1] AS a UNION ALL SELECT 2 AS b, 1 AS a FROM [1] AS b", "document", DatabaseType.Json)]
    [InlineData("SELECT 1 AS n FROM [1] AS x UNION ALL SELECT 's' AS n FROM [1] AS y UNION ALL SELECT 2 AS n FROM [1] AS z", "n", DatabaseType.Null)]
    [InlineData("SELECT 1 AS n FROM [1] AS x UNION ALL (SELECT 's' AS n FROM [1] AS y UNION ALL SELECT 2 AS n FROM [1] AS z)", "n", DatabaseType.Null)]
    [InlineData("SELECT RAW 1 FROM [1] AS x UNION ALL SELECT RAW 's' FROM [1] AS y UNION ALL SELECT RAW 2 FROM [1] AS z", "value", DatabaseType.Null)]
    [InlineData("SELECT RAW 1 FROM [1] AS x UNION ALL (SELECT RAW 's' FROM [1] AS y UNION ALL SELECT RAW 2 FROM [1] AS z)", "value", DatabaseType.Null)]
    [InlineData("SELECT NULL AS n FROM [1] AS x UNION ALL SELECT LENGTH('x') AS n FROM [1] AS y UNION ALL SELECT 2 AS n FROM [1] AS z", "n", DatabaseType.Decimal)]
    [InlineData("SELECT RAW NULL FROM [1] AS x UNION ALL SELECT RAW LENGTH('x') FROM [1] AS y UNION ALL SELECT RAW NULL FROM [1] AS z", "value", DatabaseType.Int64)]
    public async Task ExecuteAsync_HeaderShapes_ShouldBeStaticAsync(string text, string name, DatabaseType type)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-header", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string page in new[] { "", " LIMIT 0", " LIMIT 1", " OFFSET 100" })
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(text + page, cancellationToken: CancellationToken.None);
            result.Columns.ShouldHaveSingleItem().Name.ShouldBe(name);
            result.Columns[0].Type.ShouldBe(type);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: compound ordering binds either operand and parentheses page independently")]
    public async Task ExecuteAsync_OrderingAndPages_ShouldUseResultMembersAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-order", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await ValuesAsync(session, "SELECT x AS a FROM [2,1] AS x UNION ALL SELECT y AS b FROM [4,3] AS y ORDER BY a ASC NULLS LAST, b DESC LIMIT 2 OFFSET 1"))
            .ShouldBe(new[] { "{\"a\":2}", "{\"b\":4}" });
        (await ValuesAsync(session, "(SELECT x AS n FROM [3,1,2] AS x ORDER BY x LIMIT 2) UNION ALL (SELECT y AS n FROM [5,4] AS y ORDER BY y DESC LIMIT 1) ORDER BY n DESC LIMIT 2 OFFSET 1"))
            .ShouldBe(new[] { "2", "1" });
        (await ValuesAsync(session, "SELECT RAW x FROM [1,1] AS x UNION SELECT RAW y FROM [1,2] AS y UNION ALL SELECT RAW z FROM [1,1] AS z"))
            .ShouldBe(new[] { "1", "2", "1", "1" });
        (await ValuesAsync(session, "SELECT RAW x FROM [1,1] AS x UNION ALL (SELECT RAW y FROM [1,2] AS y UNION SELECT RAW z FROM [1,1] AS z)"))
            .ShouldBe(new[] { "1", "1", "1", "2" });
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNION: compound binding refusals are coded and eager")]
    [InlineData("SELECT RAW 1 FROM [1] AS a UNION SELECT RAW 2 FROM [1] AS b ORDER BY anything LIMIT 0", "CD01037")]
    [InlineData("SELECT 1 AS n FROM [1] AS a UNION SELECT 2 AS n FROM [1] AS b ORDER BY a.n LIMIT 0", "CD01038")]
    [InlineData("SELECT 1 AS n FROM [1] AS a UNION SELECT 2 AS n FROM [1] AS b ORDER BY META().id LIMIT 0", "CD01038")]
    [InlineData("SELECT 1 AS n FROM [1] AS a UNION SELECT 2 AS n FROM [1] AS b ORDER BY (SELECT RAW 1 FROM [1] AS z) LIMIT 0", "CK00001")]
    public async Task ExecuteAsync_InvalidOrder_ShouldRefuseBeforeReadingAsync(string text, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-refusal", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.Single().Code.ShouldBe(code);
        error.Diagnostics.Single().Start.ShouldNotBeNull();
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNION: hand-built nested ASTs retain eager capability backstops")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    [InlineData("SUBQUERY")]
    [InlineData("FROM")]
    public async Task Plan_DirectNestedCompound_ShouldRefuseBeforeCatalogAccessAsync(string position)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-ast-refusal", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        var compound = (OqlQueryExpression)DocumentQueryRequest.FromOql("SELECT k FROM absent UNION SELECT k FROM absent").Statement.OqlExpression;
        var subquery = new OqlSubqueryExpression(compound);
        OqlExpression expression = position switch
        {
            "INSERT" => new OqlInsertExpression("items", null, [], compound, "k", null),
            "UPSERT" => new OqlUpsertExpression("items", null, [], compound, "k", null),
            "FROM" => new OqlSelectExpression("", "q", [new OqlProjection(new OqlStarExpression(), null)], null, [], null, [], false, false, source: subquery),
            _ => new OqlSelectExpression("absent", null, [new OqlProjection(subquery, null)], null, [], null, [], false, false),
        };
        // Act / Assert
        await database.RunAsync(session, operation =>
        {
            var error = Should.Throw<DatabaseException>(() => new DocumentPlanner(database.Catalog, operation.Context.Snapshot, null).Plan(expression));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
            return ValueTask.FromResult(true);
        }, CancellationToken.None);
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION ALL: a fixed-type page pulls one row and LIMIT zero opens no source")]
    public async Task ExecuteAsync_StreamingPage_ShouldStopBeforeNextSourceAsync()
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("set-stream");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("observe", value => { calls++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await ValuesAsync(session, "SELECT observe(x) AS n FROM [1,2,3] AS x UNION ALL SELECT observe(y) AS n FROM [4,5] AS y LIMIT 1")).ShouldBe(new[] { "1" });
        calls.ShouldBe(1);
        calls = 0;
        (await ValuesAsync(session, "SELECT observe(x) AS n FROM [observe(1)] AS x UNION SELECT observe(y) AS n FROM [observe(2)] AS y ORDER BY n LIMIT 0")).ShouldBeEmpty();
        calls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: dynamic column widening observes pre-page values without retaining discarded rows")]
    public async Task ExecuteAsync_DynamicHeader_ShouldWidenBeforePagingAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-widen", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT x AS n FROM [1] AS x UNION ALL SELECT y AS n FROM ['a',2] AS y LIMIT 1", cancellationToken: CancellationToken.None);
        // Assert
        result.Columns.Single().Type.ShouldBe(DatabaseType.Null);
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        values.ShouldBe(new object?[] { 1m });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: distinct retention shares the statement grant and releases every refusal")]
    public async Task ExecuteAsync_DistinctRetention_ShouldEnforceMemoryAndReleaseAsync()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("set-memory");
        builder.Options.StatementMemoryBudget = 64 * 1024;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int index = 0; index < 80; index++)
        { await collection.PutAsync(session, index.ToString("D3", CultureInfo.InvariantCulture), Encoding.UTF8.GetBytes("{\"v\":\"" + index + new string('x', 512) + "\"}"), cancellationToken: CancellationToken.None); }
        // Act / Assert
        foreach (bool all in new[] { true, false, true })
        {
            string text = "SELECT v FROM items UNION " + (all ? "ALL " : "") + "SELECT v FROM items OFFSET 1000";
            await database.RunAsync(session, async operation =>
            {
                var memory = operation.GetStatementMemory(CancellationToken.None);
                if (all)
                {
                    await using var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation, DocumentQueryRequest.FromOql(text).Statement, null, CancellationToken.None);
                    (await CountAsync(result)).ShouldBe(0);
                }
                else
                {
                    var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation, DocumentQueryRequest.FromOql(text).Statement, null, CancellationToken.None));
                    error.Diagnostics.Single().Code.ShouldBe("CD05018");
                }
                memory.Bytes.ShouldBe(0);
                return true;
            }, CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: duplicates need no distinct-key headroom")]
    public async Task ExecuteAsync_DuplicateAtMemoryBoundary_ShouldProbeBeforeChargingAsync()
    {
        // Arrange: measure the modeled peak for one key, then leave exactly that much headroom.
        const long budget = 64 * 1024;
        var builder = DocumentDatabaseEngine.CreateBuilder("set-duplicate-boundary");
        builder.Options.StatementMemoryBudget = budget;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        const string control = "SELECT RAW 1 FROM [1] AS x UNION SELECT RAW 1 FROM [1] AS y WHERE FALSE OFFSET 1000";
        long peak = await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation,
                DocumentQueryRequest.FromOql(control).Statement, null, CancellationToken.None))
            { (await CountAsync(result)).ShouldBe(0); }
            memory.Bytes.ShouldBe(0);
            return memory.PeakBytes;
        }, CancellationToken.None);
        peak.ShouldBeLessThan(budget);
        // Act / Assert: a duplicate fits at the same peak; a new key still refuses and releases.
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            using (var padding = memory.Reserve())
            {
                padding.Charge(budget - peak);
                await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation,
                    DocumentQueryRequest.FromOql(control.Replace(" WHERE FALSE", "", StringComparison.Ordinal)).Statement, null, CancellationToken.None))
                { (await CountAsync(result)).ShouldBe(0); }
                memory.Bytes.ShouldBe(padding.Bytes);
                memory.PeakBytes.ShouldBe(budget);
                var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation,
                    DocumentQueryRequest.FromOql(control.Replace("SELECT RAW 1 FROM [1] AS y WHERE FALSE", "SELECT RAW 2 FROM [1] AS y", StringComparison.Ordinal)).Statement,
                    null, CancellationToken.None));
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
                memory.Bytes.ShouldBe(padding.Bytes);
            }
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION ALL: exactly 128 operators execute every operand in order")]
    public async Task ExecuteAsync_OperatorDepthBoundary_ShouldExecute128OperatorsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-depth", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string text = string.Join(" UNION ALL ", Enumerable.Range(0, 129).Select(index => "SELECT " + index.ToString(CultureInfo.InvariantCulture) + " AS n FROM [1] AS x"));
        // Act / Assert
        (await ValuesAsync(session, text)).ShouldBe(Enumerable.Range(0, 129).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray());
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: new distinct keys use one comparison walk within the work counter ceiling")]
    public async Task ExecuteAsync_NewDistinctKeys_ShouldUseOneProbeAsync()
    {
        // Arrange: expression-source accounting is identical in the old and new executors.
        var builder = DocumentDatabaseEngine.CreateBuilder("set-probe-work");
        builder.Options.StatementWorkBudget = 22_000;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var rows = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 1024)) + "]");
        var parameters = new Dictionary<string, object?> { ["rows"] = rows.RootElement };
        // Act / Assert: the same deterministic work ceiling applies in Debug and Release.
        for (int round = 0; round < 3; round++)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT RAW x FROM @rows AS x UNION SELECT RAW 0 FROM [] AS z", parameters, CancellationToken.None);
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
            count.ShouldBe(1024);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: equality and row processing consume the shared work budget")]
    public async Task ExecuteAsync_WorkBudget_ShouldCountUnionAndPreserveNextStatementAsync()
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("set-work");
        // Admit constructor/projection work alone; the set-operation work must exhaust this grant.
        builder.Options.StatementWorkBudget = 250;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string values = "[" + string.Join(",", Enumerable.Range(0, 100)) + "]";
        // Act / Assert
        foreach (string modifier in new[] { "", "ALL " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT RAW x FROM " + values + " AS x UNION " + modifier + "SELECT RAW y FROM [1] AS y", cancellationToken: CancellationToken.None));
            error.Diagnostics.Single().Code.ShouldBe("CD05020");
        }
        (await ValuesAsync(session, "SELECT RAW 1 FROM [1] AS x UNION SELECT RAW 1 FROM [1] AS y")).ShouldBe(new[] { "1" });
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNION: plain collection operands follow standalone SELECT work accounting")]
    [InlineData("", "")]
    [InlineData("ALL ", "")]
    [InlineData("ALL ", " ORDER BY n")]
    [InlineData("ALL ", " LIMIT 1")]
    public async Task ExecuteAsync_PlainCollectionScans_ShouldUseSelectWorkRuleAsync(string modifier, string tail)
    {
        // Arrange: scaled version of two unindexed collections with millions of residual evaluations.
        var builder = DocumentDatabaseEngine.CreateBuilder("set-plain-work");
        builder.Options.StatementWorkBudget = 50;
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string name in new[] { "a", "b" })
        {
            var collection = await session.CreateCollectionAsync(name, CancellationToken.None);
            for (int index = 0; index < 100; index++)
            { await collection.PutAsync(session, index.ToString("D3", CultureInfo.InvariantCulture), Encoding.UTF8.GetBytes("{\"id\":" + index + ",\"status\":\"" + (index == 99 ? "stuck" : "ok") + "\"}"), cancellationToken: CancellationToken.None); }
        }
        const string left = "SELECT x.id AS n FROM a x WHERE x.status = 'stuck'";
        const string right = "SELECT y.id AS n FROM b y WHERE y.status = 'stuck'";
        // Act / Assert
        (await ValuesAsync(session, left)).ShouldBe(new[] { "99" });
        (await ValuesAsync(session, right)).ShouldBe(new[] { "99" });
        var values = await ValuesAsync(session, left + " UNION " + modifier + right + tail);
        values.ShouldBe(modifier.Length == 0 || tail.Contains("LIMIT", StringComparison.Ordinal) ? new[] { "99" } : new[] { "99", "99" });
        session.LastQueryDocumentsRead.ShouldBe(200);
        // An expression source in a sibling does not turn the plain operand into a nested scan.
        (await ValuesAsync(session, left + " UNION ALL SELECT 1 AS n FROM [1] AS z")).ShouldBe(new[] { "99", "1" });
        (await ValuesAsync(session, "(" + left + " ORDER BY n) UNION ALL (" + right + " ORDER BY n)")).ShouldBe(new[] { "99", "99" });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: EXPLAIN visits both operands without executing their sources")]
    public async Task ExecuteAsync_Explain_ShouldDescribeEveryOperandAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-explain", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN SELECT 1 AS n FROM [1/0] AS a UNION ALL SELECT 2 AS n FROM [1/0] AS b ORDER BY n LIMIT 2", cancellationToken: CancellationToken.None);
        var operators = new List<string?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { operators.Add(row.GetString(2)); }
        // Assert
        operators.ShouldBe(new[] { "UnionAll", "Select", "Project", "ExpressionSource", "Select", "Project", "ExpressionSource" });
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: wildcard members and existing subqueries bind within each operand")]
    public async Task ExecuteAsync_OperandBindings_ShouldKeepIndependentScopesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-bindings", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var items = await session.CreateCollectionAsync("items", CancellationToken.None);
        await items.PutAsync(session, "a", "{\"n\":2}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await items.PutAsync(session, "b", "{\"n\":1}"u8.ToArray(), cancellationToken: CancellationToken.None);
        // Act / Assert
        (await ValuesAsync(session, "SELECT * FROM items UNION ALL SELECT n FROM items ORDER BY n LIMIT 2"))
            .ShouldBe(new[] { "{\"n\":1}", "{\"n\":1}" });
        (await ValuesAsync(session, "SELECT RAW (SELECT RAW items.n FROM [1] AS seed) FROM items UNION SELECT RAW [1] FROM [1] AS seed"))
            .ShouldBe(new[] { "[2]", "[1]" });
        (await ValuesAsync(session, "SELECT RAW x FROM [1.00] AS x UNION SELECT RAW y FROM [1] AS y"))
            .ShouldBe(new[] { "1.00" });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: cancellation releases source distinct sort and output reservations")]
    public async Task ExecuteAsync_CanceledProjection_ShouldReleaseEveryReservationAsync()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("set-cancel");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("observe", value =>
        { if (++calls == 3) { cancellation.Cancel(); } return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string text = "SELECT observe(x) AS n FROM [1,2,3,4] AS x UNION SELECT 5 AS n FROM [1] AS y ORDER BY n";
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            await Should.ThrowAsync<OperationCanceledException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation,
                DocumentQueryRequest.FromOql(text).Statement, null, cancellation.Token));
            calls.ShouldBe(3);
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: sorter work refusal preserves CD05020 and releases its buffer")]
    public async Task ExecuteAsync_SortWorkBudget_ShouldPreserveCodedFailureAsync()
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("set-sort-work");
        builder.Options.StatementWorkBudget = 280;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("observe", value => { calls++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var values = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 64).Reverse()) + "]");
        var parameters = new Dictionary<string, object?> { ["rows"] = values.RootElement };
        string text = "SELECT observe(x) AS n FROM @rows AS x UNION ALL SELECT 0 AS n FROM [] AS y ORDER BY n";
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await DocumentPlanExecutor.ExecuteAsync(database, operation,
                DocumentQueryRequest.FromOql(text).Statement, parameters, CancellationToken.None));
            error.Diagnostics.Single().Code.ShouldBe("CD05020");
            calls.ShouldBe(64);
            memory.Bytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNION: a commit between operands cannot change the statement snapshot")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task ExecuteAsync_BetweenOperandCommit_ShouldKeepOneSnapshotAsync(IsolationLevel isolation)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int calls = 0;
        DocumentDatabaseSession? writer = null;
        DocumentCollection? target = null;
        byte[] changed = "{\"v\":2}"u8.ToArray();
        var builder = DocumentDatabaseEngine.CreateBuilder("set-snapshot");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("publish", value =>
        {
            calls++;
            // The commit finishes before the right operand is opened; no timing decides the outcome.
            target!.PutAsync(writer!, "a", changed, cancellationToken: guard.Token).AsTask().GetAwaiter().GetResult();
            return value;
        }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await using var writerLease = await database.CreateSessionAsync(guard.Token);
        writer = writerLease;
        var items = await session.CreateCollectionAsync("items", guard.Token);
        await items.PutAsync(session, "a", "{\"v\":1}"u8.ToArray(), cancellationToken: guard.Token);
        target = await writer.GetCollectionAsync("items", guard.Token);
        await using var transaction = await session.BeginTransactionAsync(isolation, guard.Token);
        // Act
        var values = await ValuesAsync(session, "SELECT publish(v) AS n FROM items UNION ALL SELECT v AS n FROM items");
        // Assert
        calls.ShouldBe(1);
        values.ShouldBe(new[] { "1", "1" });
        await transaction.RollbackAsync(guard.Token);
        (await ValuesAsync(session, "SELECT RAW v FROM items")).ShouldBe(new[] { "2" });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNION: identical multi-member rows retain ordered typed columns and widening")]
    public async Task ExecuteAsync_MultipleNamedColumns_ShouldRetainOrderAndWidenAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("set-columns", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT LENGTH('x') AS a,2 AS b FROM [1] AS x UNION ALL SELECT 3 AS a,'s' AS b FROM [1] AS y", cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add([row.GetValue(0), row.GetValue(1)]); }
        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(new[] { "a", "b" });
        result.Columns.Select(column => column.Type).ShouldBe(new[] { DatabaseType.Decimal, DatabaseType.Null });
        rows[0].ShouldBe(new object?[] { 1m, 2m });
        rows[1].ShouldBe(new object?[] { 3m, "s" });
    }

    private static async Task<int> CountAsync(DatabaseResultSet result)
    { int count = 0; await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; } return count; }

    private static async Task<string[]> ValuesAsync(DocumentDatabaseSession session, string text)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(text, cancellationToken: CancellationToken.None);
        var values = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetString(0)!); }
        return values.ToArray();
    }

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDecimal().ToString("G29", CultureInfo.InvariantCulture),
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => property.Name + ":" + Canonical(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText(),
    };

    private static string Project(JsonElement value, bool named, bool reverse)
        => !named ? value.GetRawText() : "{" + string.Join(",", (reverse ? new[] { "b", "a" } : new[] { "a", "b" })
            .Where(name => value.TryGetProperty(name, out _)).Select(name => "\"" + name + "\":" + value.GetProperty(name).GetRawText())) + "}";
}
