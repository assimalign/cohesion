using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlJsonAccessTests
{
    private readonly ITestOutputHelper _output;

    public SqlJsonAccessTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON: generated documents agree with PostgreSQL and owner extraction rules (#1722)")]
    public void Evaluate_GeneratedDocuments_ShouldMatchPostgresOracle()
    {
        // Arrange: an independent value model renders JSON and computes expected members.
        // PostgreSQL jsonfuncs.c:886-1005,1160-1296: last duplicate, signed indexes,
        // JSON null and string de-escaping. The owner requires every non-array root,
        // including PostgreSQL's raw-scalar JSONB wrapper at 0/-1, to return NULL.
        // No JSON parser builds the oracle.
        var parameters = new Dictionary<string, object?>();
        var evaluator = new SqlExpressionEvaluator([], parameters);
        int comparisons = 0;
        // Act / Assert
        foreach (var document in Documents())
        {
            parameters["doc"] = document.Raw;
            foreach (object selector in new object[] { "a", "dup", "é", "nested", "missing", "", "0", -2147483648, -7, -4, -3, -2, -1, 0, 1, 2, 3, 4, 7, 2147483647 })
            {
                parameters["selector"] = selector;
                var selected = document.Select(selector);
                foreach (string type in new[] { "JSON", "JSONB" })
                {
                    foreach (bool text in new[] { false, true })
                    {
                        var actual = evaluator.Evaluate(Expression($"CAST(@doc AS {type}) {(text ? "->>" : "->")} @selector"), []);
                        if (actual is byte[] bytes) { actual = Encoding.UTF8.GetString(bytes); }
                        actual.ShouldBe(text && selected?.IsNull == true ? null : text ? selected?.Text ?? selected?.Raw : selected?.Raw);
                        comparisons++;
                    }
                }
            }
        }
        comparisons.ShouldBe(10_240);
        _output.WriteLine($"{comparisons} PostgreSQL/owner extraction oracle comparisons");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON: IS JSON validates text and distinguishes every kind (#1722)")]
    [InlineData("null", false, false, true)]
    [InlineData("true", false, false, true)]
    [InlineData("123.5e-2", false, false, true)]
    [InlineData("\"café\\uD83D\\uDE00\"", false, false, true)]
    [InlineData(" [1, null] ", true, false, false)]
    [InlineData("{\"a\":1,\"a\":2}", false, true, false)]
    public async Task Execute_IsJsonKinds_ShouldMatchReferenceAsync(string text, bool array, bool obj, bool scalar)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string operand in new[] { "@doc", "CAST(@doc AS JSON)", "CAST(@doc AS JSONB)", "CAST(@doc AS CHAR(1000))" })
        {
            var request = SqlQueryRequest.FromSql($"SELECT {operand} IS JSON, {operand} IS JSON VALUE, {operand} IS JSON ARRAY, {operand} IS JSON OBJECT, {operand} IS JSON SCALAR, {operand} IS NOT JSON SCALAR", new Dictionary<string, object?> { ["doc"] = text });
            await using var result = (await session.ExecuteAsync(request, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            result.Columns.Select(column => column.Type).ShouldAllBe(type => type == DatabaseType.Boolean);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { Enumerable.Range(0, 6).Select(row.GetValue).ShouldBe(new object?[] { true, true, array, obj, scalar, !scalar }); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON: malformed text returns FALSE and NOT returns TRUE for every kind (#1722)")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{\"a\":}")]
    [InlineData("[1,]")]
    [InlineData("1 2")]
    [InlineData("/*comment*/{}")]
    [InlineData("NaN")]
    [InlineData("\"\\x\"")]
    [InlineData("\"unterminated")]
    public void Evaluate_MalformedPredicate_ShouldReturnFalse(string text)
    {
        // Arrange
        var evaluator = new SqlExpressionEvaluator([], new Dictionary<string, object?> { ["doc"] = text });
        // Act / Assert
        foreach (string kind in new[] { "", " VALUE", " ARRAY", " OBJECT", " SCALAR" })
        {
            evaluator.Evaluate(Expression("@doc IS JSON" + kind), []).ShouldBe(false);
            evaluator.Evaluate(Expression("@doc IS NOT JSON" + kind), []).ShouldBe(true);
            evaluator.Evaluate(Expression("NULL IS JSON" + kind), []).ShouldBeNull();
            evaluator.Evaluate(Expression("NULL IS NOT JSON" + kind), []).ShouldBeNull();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON: typed extraction, negative literals, casts and NULLs execute (#1722)")]
    [InlineData("'{\"a\":{\"b\":\"café\\uD83D\\uDE00\"}}'::JSON -> 'a' ->> 'b'", "café😀")]
    [InlineData("'[1,2,3]'::JSON ->> -1", "3")]
    [InlineData("'[1,2,3]'::JSON ->> -3", "1")]
    [InlineData("'[1,2,3]'::JSON ->> -4", null)]
    [InlineData("'[1,2,3]'::JSON ->> -2147483648", null)]
    [InlineData("'[1,2,3]'::JSON ->> 2147483647", null)]
    [InlineData("'{}'::JSON ->> 0", null)]
    [InlineData("'[]'::JSON ->> '0'", null)]
    [InlineData("'null'::JSON -> 'a'", null)]
    [InlineData("NULL::JSONB -> 'a'", null)]
    [InlineData("CAST((SELECT NULL) AS JSON) -> 'a'", null)]
    [InlineData("(CASE WHEN FALSE THEN '{}'::JSON ELSE NULL END) -> 'a'", null)]
    [InlineData("COALESCE(NULL,NULL::JSONB) ->> 0", null)]
    [InlineData("'{}'::JSON -> NULL", null)]
    [InlineData("'1'::JSON ->> 0", null)]
    [InlineData("('{\"a\":\"A\"}'::JSON ->> ('a' COLLATE case_insensitive)) = 'a'", true)]
    [InlineData("('{\"a\":\"A\"}'::JSONB ->> ('a' COLLATE binary)) = 'a'", false)]
    public async Task Execute_ExtractionCorpus_ShouldReturnValueAsync(string expression, object? expected)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT " + expression)).ShouldHaveSingleItem()[0].ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON: scalar conversions use JSON kinds, exponent numbers and PostgreSQL rounding (#1722)")]
    [InlineData("12.5", "INT", 13)]
    [InlineData("-12.5", "SMALLINT", (short)-13)]
    [InlineData("12.5", "BIGINT", 13L)]
    [InlineData("127", "TINYINT", (sbyte)127)]
    [InlineData("1.25e2", "INT", 125)]
    [InlineData("1.25", "REAL", 1.25f)]
    [InlineData("1.25", "DOUBLE PRECISION", 1.25d)]
    [InlineData("-0.0", "REAL", 0f)]
    [InlineData("-0.0", "DOUBLE PRECISION", 0d)]
    [InlineData("true", "BOOLEAN", true)]
    [InlineData("false", "BOOLEAN", false)]
    [InlineData("null", "BOOLEAN", null)]
    [InlineData("null", "NUMERIC(4,2)", null)]
    [InlineData("\"x\"", "TEXT", "\"x\"")]
    public async Task Execute_ScalarCasts_ShouldMatchReferenceAsync(string json, string target, object? expected)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string source in new[] { "JSON", "JSONB" })
        {
            var actual = (await SqlPredicateExecutionTests.RowsAsync(session, $"SELECT CAST(CAST('{json}' AS {source}) AS {target})")).ShouldHaveSingleItem()[0];
            actual.ShouldBe(expected);
            if (actual is not null) { actual.GetType().ShouldBe(expected!.GetType()); }
            if (actual is float real && real == 0) { BitConverter.SingleToInt32Bits(real).ShouldBe(0); }
            if (actual is double floating && floating == 0) { BitConverter.DoubleToInt64Bits(floating).ShouldBe(0L); }
            (await SqlPredicateExecutionTests.RowsAsync(session, $"SELECT CAST(CAST('12.345' AS {source}) AS NUMERIC(4,2))")).ShouldHaveSingleItem()[0].ShouldBe(12.35m);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON: incorrect kinds and numeric range failures retain coded diagnostics (#1722)")]
    [InlineData("{}", "INT", "CS02079")]
    [InlineData("[]", "BOOLEAN", "CS02079")]
    [InlineData("\"123\"", "NUMERIC", "CS02079")]
    [InlineData("1", "BOOLEAN", "CS02079")]
    [InlineData("true", "REAL", "CS02079")]
    [InlineData("32767.5", "SMALLINT", "CS02002")]
    [InlineData("2147483648", "INT", "CS02002")]
    [InlineData("1e1000", "DOUBLE PRECISION", "CS02002")]
    [InlineData("1e-1000", "REAL", "CS02002")]
    public async Task Execute_InvalidScalarCasts_ShouldRefuseAsync(string json, string target, string code)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string source in new[] { "JSON", "JSONB" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session, $"SELECT CAST(CAST('{json}' AS {source}) AS {target})"));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON: invalid signatures refuse before rows or skipped alternatives (#1722)")]
    [InlineData("'{}' -> 'a'")]
    [InlineData("NULL -> 'a'")]
    [InlineData("NULL -> NULL")]
    [InlineData("(SELECT NULL) -> 'a'")]
    [InlineData("1 -> 'a'")]
    [InlineData("'{}'::JSON -> 1::BIGINT")]
    [InlineData("'{}'::JSON -> TRUE")]
    [InlineData("1 IS JSON")]
    [InlineData("(CASE WHEN TRUE THEN DATE '2024-01-01' ELSE TIMESTAMP '2024-01-01 00:00:00' END) IS JSON")]
    [InlineData("(CASE WHEN TRUE THEN '{}'::JSON ELSE @bad END) -> 'a'")]
    [InlineData("'{}'::JSON -> COALESCE(1,@bad)")]
    public async Task Execute_InvalidSignatures_ShouldBindBeforeInputAsync(string expression)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE empty_rows(id INT)");
        // Act / Assert
        var request = SqlQueryRequest.FromSql("SELECT " + expression + " FROM empty_rows", new Dictionary<string, object?> { ["bad"] = new object() });
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01067");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON: assignment validates JSON and JSONB and preserves raw storage (#1722)")]
    public async Task Execute_AssignmentValidation_ShouldBeAtomicAndPreserveBytesAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE j(id INT, doc JSON, bin JSONB)");
        const string raw = " { \"a\":1, \"a\":2 } ";
        var request = SqlQueryRequest.FromSql("INSERT INTO j VALUES(1,@doc,CAST(@doc AS JSONB))", new Dictionary<string, object?> { ["doc"] = raw });
        await session.ExecuteAsync(request, CancellationToken.None);
        // Act / Assert
        foreach (string sql in new[] { "INSERT INTO j VALUES(2,'{}',NULL),(3,'broken',NULL)", "UPDATE j SET doc='broken'", "UPDATE j SET bin=X'FF'" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.ExecuteAsync(session, sql));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02078");
        }
        var row = (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT doc,bin,doc->>'a',bin->>'a' FROM j")).ShouldHaveSingleItem();
        row[0].ShouldBe(raw); row[1].ShouldBeOfType<byte[]>().ShouldBe(Encoding.UTF8.GetBytes(raw));
        row[2].ShouldBe("2"); row[3].ShouldBe("2");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON: malformed access, strict UTF-8 and depth limits stay distinct from missing values (#1722)")]
    public void Extract_InvalidInput_ShouldRefuseBeforeMissingMember()
    {
        // Arrange
        object[] invalid = ["{broken", new byte[] { 0xFF }, new string('[', 129) + "0" + new string(']', 129), "\"\uD800\""];
        // Act / Assert
        foreach (object value in invalid)
        {
            SqlJsonAccess.IsJson(value, SqlJsonKind.Value).ShouldBeFalse();
            Should.Throw<SqlExecutionException>(() => SqlJsonAccess.Extract(value, "missing", DatabaseType.Json, false))
                .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02078");
        }
        foreach (string escaped in new[] { "{\"a\":\"\\uD800\"}", "{\"a\":\"\\u0000\"}", "{\"\\uD800\":1}" })
        {
            SqlJsonAccess.IsJson(escaped, SqlJsonKind.Value).ShouldBeTrue();
            Should.Throw<SqlExecutionException>(() => SqlJsonAccess.Extract(escaped, "missing", DatabaseType.Json, false))
                .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02078");
        }
        string atLimit = new string('[', 128) + "0" + new string(']', 128);
        SqlJsonAccess.IsJson(atLimit, SqlJsonKind.Array).ShouldBeTrue();
        Should.Throw<SqlPlanningException>(() => SqlJsonAccess.Extract(123, "missing", DatabaseType.Json, false))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01067");
        foreach (string obj in new[] { "{}", "{\"a\":1}" })
        {
            Should.Throw<SqlExecutionException>(() => SqlJsonAccess.Extract(obj, "\uD800", DatabaseType.Json, false))
                .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02078");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON: grouped, correlated and window results retain JSON typing (#1722)")]
    public async Task Execute_ComposedExpressions_ShouldPreserveTypesAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE j(id INT,doc JSONB)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO j VALUES(1,'{\"a\":[2]}'::JSONB),(2,'{\"a\":[3]}'::JSONB)");
        // Act / Assert
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT doc->'a'->>0,COUNT(*) FROM j GROUP BY doc->'a'->>0 ORDER BY 1"))
            .ShouldBe(new object?[][] { ["2", 1L], ["3", 1L] });
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT COALESCE(NULL::JSONB,(SELECT doc FROM j WHERE id=o.id))->'a'->>0 FROM j o ORDER BY id"))
            .ShouldBe(new object?[][] { ["2"], ["3"] });
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT doc->'a'->>(SELECT o.id-1)::INT FROM j o ORDER BY id"))
            .ShouldBe(new object?[][] { ["2"], [null] });
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT (FIRST_VALUE(doc) OVER(ORDER BY id))->'a'->>0 FROM j ORDER BY id"))
            .ShouldBe(new object?[][] { ["2"], ["2"] });
        await using var empty = (await session.ExecuteAsync("SELECT doc->'a',doc->>'a',doc IS JSON ARRAY FROM j WHERE FALSE", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        empty.Columns.Select(column => column.Type).ShouldBe(new[] { DatabaseType.JsonBinary, DatabaseType.String, DatabaseType.Boolean });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON: canonical views, defaults and CHECK constraints survive restart (#1722)")]
    public async Task Open_JsonDefinitions_ShouldRoundTripAsync()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("json", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE j(doc JSON DEFAULT '{\"a\":[1,null,3]}'::JSON, n INT DEFAULT ('{\"n\":2}'::JSON->'n')::INT, CHECK(doc IS JSON OBJECT AND doc->'a' IS JSON ARRAY))");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE VIEW jv AS SELECT doc->'a'->>-1 AS last,n,doc IS NOT JSON SCALAR AS obj FROM j");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO j DEFAULT VALUES");
        }
        // Act
        await using var reopened = SqlDatabaseEngine.Create("json", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(reader, "INSERT INTO j DEFAULT VALUES");
        // Assert
        (await SqlPredicateExecutionTests.RowsAsync(reader, "SELECT * FROM jv")).ShouldBe(new object?[][] { ["3", 2, true], ["3", 2, true] });
        await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.ExecuteAsync(reader, "INSERT INTO j VALUES('[]',0)"));
        (await SqlPredicateExecutionTests.RowsAsync(reader, "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='j' AND COLUMN_NAME='n'"))
            .ShouldHaveSingleItem()[0].ShouldBe("CAST(CAST('{\"n\":2}' AS JSON) -> 'n' AS INT)");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON: extraction allocation grows linearly with input size using three-round minima (#1722)")]
    public void Extract_GrowingInput_ShouldHaveLinearAllocation()
    {
        // Arrange: parse padding of two sizes but return the same single character.
        string small = "{\"padding\":\"" + new string('x', 4_096) + "\",\"n\":1}";
        string large = "{\"padding\":\"" + new string('x', 65_536) + "\",\"n\":1}";
        Measure(small); Measure(large);
        // Act
        long first = long.MaxValue, second = long.MaxValue;
        for (int round = 0; round < 3; round++) { first = Math.Min(first, Measure(small)); second = Math.Min(second, Measure(large)); }
        // Assert: 16x input, fixed-sized result; no retained document or quadratic copy chain.
        first.ShouldBeGreaterThan(0);
        second.ShouldBeLessThanOrEqualTo(first * 17);
        _output.WriteLine($"JSON extraction bytes/op: 4KiB={first / 128d}; 64KiB={second / 128d}; min of 3 rounds, 128 operations");

        static long Measure(string input)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 128; i++) { _ = SqlJsonAccess.Extract(input, "n", DatabaseType.Json, asText: true); }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON chains: folded and cast-separated steps agree with the extraction oracle (#1722)")]
    public void Evaluate_ChainedCorpus_ShouldMatchUnfoldedStepsAndOracle()
    {
        // Arrange: reuse the independent corpus; a CAST forces step-by-step evaluation.
        object[] selectors = ["a", "dup", "é", "nested", "missing", "", "0", int.MinValue, -7, -4, -3, -2, -1, 0, 1, 2, 3, 4, 7, int.MaxValue];
        int comparisons = 0;
        // Act / Assert
        foreach (string type in new[] { "JSON", "JSONB" })
        {
            var source = type == "JSON" ? DatabaseType.Json : DatabaseType.JsonBinary;
            var documents = Documents().Select(document => (Oracle: document, Value: source == DatabaseType.Json ? (object)document.Raw : Encoding.UTF8.GetBytes(document.Raw))).ToArray();
            foreach (object first in selectors)
            {
                foreach (object second in selectors)
                {
                    var evaluator = new SqlExpressionEvaluator([new Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogColumn("doc", new(source))],
                        new Dictionary<string, object?> { ["first"] = first, ["second"] = second });
                    foreach (bool text in new[] { false, true })
                    {
                        string arrow = text ? "->>" : "->";
                        var chain = evaluator.Bind(Expression($"doc->@first {arrow} @second"));
                        var unfolded = evaluator.Bind(Expression($"CAST(doc->@first AS {type}) {arrow} @second"));
                        foreach (var document in documents)
                        {
                            object?[] row = [document.Value];
                            object? actual = evaluator.Evaluate(chain, row);
                            object? expected = evaluator.Evaluate(unfolded, row);
                            if (actual is byte[] bytes) { actual = Encoding.UTF8.GetString(bytes); }
                            if (expected is byte[] raw) { expected = Encoding.UTF8.GetString(raw); }
                            actual.ShouldBe(expected);
                            var selected = document.Oracle.Select(first)?.Select(second);
                            actual.ShouldBe(text && selected?.IsNull == true ? null : text ? selected?.Text ?? selected?.Raw : selected?.Raw);
                            comparisons++;
                        }
                        evaluator.Evaluate(chain, [null]).ShouldBeNull();
                    }
                }
            }
        }
        comparisons.ShouldBe(204_800);
        _output.WriteLine($"{comparisons} chained/unfolded/oracle comparisons, plus 1600 SQL NULL chains");
    }

    private static SqlExpression Expression(string text)
    {
        var statement = new SqlQueryParser().Parse("SELECT " + text).ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message).ShouldBeEmpty();
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression;
    }

    private static IEnumerable<JsonOracle> Documents()
    {
        for (int seed = 0; seed < 32; seed++)
        {
            var number = new JsonOracle(seed.ToString(CultureInfo.InvariantCulture));
            var unicode = new JsonOracle("\"caf\\u00e9\\uD83D\\uDE00\"", "café😀");
            var child = JsonOracle.Object(("nested", number), ("é", unicode));
            var array = JsonOracle.Array(new JsonOracle("null"), number, unicode, child);
            yield return array;
            yield return JsonOracle.Object(("a", array), ("dup", number), ("dup", child), ("é", unicode));
            yield return seed % 2 == 0 ? unicode : new JsonOracle("null");
            yield return JsonOracle.Array(child, array, JsonOracle.Object(("a", child)), new JsonOracle("true"));
        }
    }

    private sealed record JsonOracle(string Raw, string? Text = null, JsonOracle[]? Elements = null, (string Key, JsonOracle Value)[]? Members = null)
    {
        internal bool IsNull => Raw == "null";
        internal static JsonOracle Array(params JsonOracle[] elements) => new("[ " + string.Join(", ", elements.Select(value => value.Raw)) + " ]", Elements: elements);
        internal static JsonOracle Object(params (string Key, JsonOracle Value)[] members)
            => new("{ " + string.Join(", ", members.Select(member => "\"" + member.Key + "\": " + member.Value.Raw)) + " }", Members: members);
        internal JsonOracle? Select(object selector)
        {
            if (selector is string key) { return Members?.LastOrDefault(member => member.Key == key).Value; }
            if (Elements is null) { return null; }
            long index = (int)selector;
            if (index < 0) { index += Elements.Length; }
            return index < 0 || index >= Elements.Length ? null : Elements[index];
        }
    }
}
