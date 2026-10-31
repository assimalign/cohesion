using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlJsonPathFixTests
{
    private readonly ITestOutputHelper _output;

    public SqlJsonPathFixTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - SQL/JSON: path parameters use ordinary parameter names (#1722)")]
    [InlineData("$1", "1")]
    [InlineData("$path", "path")]
    [InlineData("@path", "path")]
    public async Task Execute_PathParameterPrefixes_ShouldResolveBareNamesAsync(string parameter, string key)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = SqlQueryRequest.FromSql("SELECT JSON_VALUE('{\"a\":1}', " + parameter + ")",
            new Dictionary<string, object?> { [key] = "$.a" });
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
        // Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe("1"); count++; }
        count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SQL/JSON: Boolean DEFAULT executes the selected assignment cast (#1722)")]
    [InlineData("'$.missing'", "EMPTY")]
    [InlineData("'strict $.missing'", "ERROR")]
    public async Task Execute_BooleanDefaultOverride_ShouldUseBoundCastAsync(string path, string behavior)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("json-cast");
        builder.Types.AddCast(SqlCast.Create<bool, int>(SqlType.Boolean, SqlType.Integer, static _ => 77, SqlCastContext.Assignment));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await SqlPredicateExecutionTests.RowsAsync(session,
            $"SELECT CAST(TRUE AS INT), JSON_VALUE('{{}}', {path} RETURNING INT DEFAULT TRUE ON {behavior})");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 77, 77 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SQL/JSON: unconstrained JSON domains use scalar I/O coercion (#1722)")]
    [InlineData("JSON")]
    [InlineData("JSONB")]
    public async Task Execute_JsonDomainWithoutCheck_ShouldUseDomainCoercionAsync(string type)
    {
        // Arrange: parse_expr.c:4727-4733, execExprInterp.c:4977-4990,5016-5035.
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE DOMAIN j AS " + type);
        // Act
        var rows = await SqlPredicateExecutionTests.RowsAsync(session,
            "SELECT JSON_VALUE('\"hello\"', '$' RETURNING j), JSON_VALUE('true', '$' RETURNING j) IS NULL");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { null, true });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SQL/JSON: indirect definitions retain the statement budget (#1722)")]
    [InlineData("view")]
    [InlineData("default")]
    [InlineData("check")]
    [InlineData("function")]
    [InlineData("domain")]
    [InlineData("index")]
    [InlineData("count")]
    [InlineData("alter-check")]
    public async Task Execute_IndirectJsonDefinition_ShouldRetainBudgetAsync(string definition)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("json-definition-budget", new() { StatementMemoryBudget = 32_768 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE input(doc JSON)");
        string document = "[" + string.Join(',', Enumerable.Repeat("1", 1000)) + "]";
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO input VALUES ('" + document + "')");
        string statement;
        if (definition == "view")
        {
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE VIEW v AS SELECT JSON_QUERY(doc, '$[*]' WITH WRAPPER) AS doc FROM input");
            statement = "SELECT doc FROM v";
        }
        else if (definition == "default")
        {
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE FUNCTION payload() RETURNS TEXT VOLATILE RETURN '" + document + "'");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE target(doc JSONB DEFAULT JSON_QUERY(payload(), '$[*]' WITH WRAPPER))");
            statement = "INSERT INTO target DEFAULT VALUES";
        }
        else if (definition == "function")
        {
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE FUNCTION expand_json(p TEXT) RETURNS JSONB VOLATILE RETURN JSON_QUERY(p, '$[*]' WITH WRAPPER)");
            statement = "SELECT expand_json(doc::TEXT) FROM input";
        }
        else if (definition == "alter-check")
        {
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE FUNCTION has_json(p JSON) RETURNS BOOLEAN IMMUTABLE RETURN JSON_QUERY(p, '$[*]' WITH WRAPPER) IS NOT NULL");
            statement = "ALTER TABLE input ADD CONSTRAINT budgeted CHECK(has_json(doc))";
        }
        else if (definition == "count")
        {
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE FUNCTION count_json(p TEXT) RETURNS INT VOLATILE RETURN CASE WHEN JSON_QUERY(p, '$[*]' RETURNING TEXT WITH WRAPPER) IS NULL THEN 0 ELSE 1 END");
            statement = "SELECT 1 LIMIT count_json('" + document + "')";
        }
        else if (definition == "domain")
        {
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE DOMAIN guarded AS JSON CHECK(JSON_QUERY(VALUE, '$[*]' WITH WRAPPER) IS NOT NULL)");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE target(doc guarded)");
            statement = "INSERT INTO target SELECT doc FROM input";
        }
        else if (definition == "index")
        {
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE target(id INT, doc JSON)");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE INDEX ix ON target(id) WHERE JSON_QUERY(doc, '$[*]' WITH WRAPPER) IS NOT NULL");
            statement = "INSERT INTO target SELECT 1,doc FROM input";
        }
        else
        {
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE target(doc JSON, CHECK(JSON_QUERY(doc, '$[*]' WITH WRAPPER) IS NOT NULL))");
            statement = "INSERT INTO target SELECT doc FROM input";
        }
        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));
        failure.Diagnostics[0].Code.ShouldBe("CS05094");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SQL/JSON: 100000-item wildcards retain their complete work budget (#1722)")]
    [InlineData(false, 300_002)]
    [InlineData(true, 400_002)]
    public void Evaluate_LargeWildcard_ShouldSucceedWithinBudget(bool query, long visits)
    {
        // Arrange
        var evaluator = new SqlExpressionEvaluator([], new Dictionary<string, object?>
            { ["doc"] = "[" + string.Join(',', Enumerable.Repeat("1", 100_000)) + "]" });
        var expression = evaluator.Bind(Expression(query
            ? "JSON_QUERY(@doc, 'strict $[0 to last]' RETURNING TEXT WITH WRAPPER ERROR ON ERROR)"
            : "JSON_VALUE(@doc, 'strict $[0 to last]')"));
        using var work = new SqlJsonPathExecution(budget: 64L * 1024 * 1024);
        work.Enter();
        // Act
        object? result = evaluator.Evaluate(expression, []);
        // Assert
        if (query) { result.ShouldBeOfType<string>().Length.ShouldBe(300_000); }
        else { result.ShouldBeNull(); }
        work.Visits.ShouldBe(visits);
        work.Reserved.ShouldBe(0);
        _output.WriteLine($"100000-item {(query ? "QUERY" : "VALUE")}: {work.Visits} visits, {work.Reserved} remaining reservation");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: only reached write definitions activate an execution scope (#1722)")]
    public async Task Execute_SelectedWriteDefinitions_ShouldActivateOnlyTheirPathsAsync()
    {
        // Arrange: a volatile observer records the actual scope at evaluation time.
        var builder = SqlDatabaseEngine.CreateBuilder("json-scope");
        builder.Functions.Add(SqlScalarFunction.Create<int, bool>("json_scope", static _ => SqlJsonPathExecution.Current is not null, SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE FUNCTION payload() RETURNS TEXT VOLATILE RETURN '1'");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE defaults(id INT DEFAULT JSON_VALUE(payload(), '$' RETURNING INT))");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE checked(id TEXT, CHECK(JSON_EXISTS(id, '$')))");
        // Act / Assert
        foreach (var (sql, expected) in new (string, bool)[]
        {
            ("INSERT INTO defaults VALUES(DEFAULT) RETURNING json_scope(0)", true),
            ("INSERT INTO defaults VALUES(2) RETURNING json_scope(0)", false),
            ("UPDATE defaults SET id=DEFAULT WHERE id=2 RETURNING json_scope(0)", true),
            ("UPDATE defaults SET id=3 WHERE id=1 RETURNING json_scope(0)", false),
            ("DELETE FROM defaults RETURNING json_scope(0)", false),
            ("INSERT INTO checked VALUES('1') RETURNING json_scope(0)", true),
            ("SELECT json_scope(0) FROM checked", false),
            ("DELETE FROM checked RETURNING json_scope(0)", false),
        })
        {
            var rows = await SqlPredicateExecutionTests.RowsAsync(session, sql);
            rows.ShouldNotBeEmpty();
            rows.ShouldAllBe(row => Equals(row[0], expected));
        }
    }

#if !DEBUG
    [Theory(DisplayName = "Cohesion Test [Sql] - SQL/JSON: narrow path allocation stays flat in unselected padding (#1722)")]
    [InlineData("text")]
    [InlineData("escaped")]
    [InlineData("number")]
    public void Evaluate_NarrowParameterizedPath_ShouldAllocateForResult(string paddingKind)
    {
        // Arrange: bind and warm both document sizes outside the measured rounds.
        int[] sizes = [1_000, 100_000];
        var evaluators = new SqlExpressionEvaluator[2];
        var expressions = new SqlBoundExpression[2];
        for (int index = 0; index < sizes.Length; index++)
        {
            string padding = paddingKind == "number" ? new string('1', sizes[index])
                : "\"" + (paddingKind == "escaped" ? string.Concat(Enumerable.Repeat("\\u0078", sizes[index])) : new string('x', sizes[index])) + "\"";
            evaluators[index] = new SqlExpressionEvaluator([], new Dictionary<string, object?>
                { ["doc"] = "{\"n\":1,\"padding\":" + padding + "}" });
            expressions[index] = evaluators[index].Bind(Expression("JSON_VALUE(@doc, '$.n' RETURNING INT)"));
            Measure(index, 100);
        }
        // Act
        long[] minima = [long.MaxValue, long.MaxValue];
        for (int round = 0; round < 3; round++)
        { for (int index = 0; index < sizes.Length; index++) { minima[index] = Math.Min(minima[index], Measure(index, 100)); } }
        // Assert
        _output.WriteLine($"Narrow path bytes/evaluation (padding={paddingKind}), minimum of 3 warmed rounds: 1000={minima[0]}, 100000={minima[1]}");
        minima[0].ShouldBeLessThanOrEqualTo(4_096);
        minima[1].ShouldBeLessThanOrEqualTo(4_096);
        minima[1].ShouldBeLessThanOrEqualTo(minima[0] + 256);

        long Measure(int index, int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < count; iteration++) { evaluators[index].Evaluate(expressions[index], []).ShouldBe(1); }
            return (GC.GetAllocatedBytesForCurrentThread() - before) / count;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: unrelated and dropped definitions add no query scope allocation (#1722)")]
    public async Task Execute_UnrelatedAndDroppedJsonDefinitions_ShouldRetainOrdinaryAllocationsAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE t(a INT)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO t VALUES(1)");
        var request = SqlQueryRequest.FromSql("SELECT a FROM t");
        await using var transaction = await session.BeginTransactionAsync(Assimalign.Cohesion.Database.Transactions.IsolationLevel.ReadCommitted, CancellationToken.None);
        long before = Minimum();
        await transaction.RollbackAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE unused(a INT, doc JSON, CHECK(JSON_EXISTS(doc, '$')))");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO unused(a) VALUES(1)");
        await using var withDefinition = await session.BeginTransactionAsync(Assimalign.Cohesion.Database.Transactions.IsolationLevel.ReadCommitted, CancellationToken.None);
        long during = Minimum();
        request = SqlQueryRequest.FromSql("SELECT a FROM unused");
        long readCheckedTable = Minimum();
        request = SqlQueryRequest.FromSql("SELECT a FROM t");
        await withDefinition.RollbackAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "DROP TABLE unused");
        await using var afterDrop = await session.BeginTransactionAsync(Assimalign.Cohesion.Database.Transactions.IsolationLevel.ReadCommitted, CancellationToken.None);
        // Act
        long after = Minimum();
        // Assert: ceilings, not equality; exact minima vary across platforms.
        _output.WriteLine($"Ordinary SELECT bytes/query, minimum of 3 warmed rounds: before={before}, unrelated JSON CHECK={during}, read checked table={readCheckedTable}, after DROP={after}");
        before.ShouldBeLessThanOrEqualTo(4_280);
        during.ShouldBeLessThanOrEqualTo(4_280);
        readCheckedTable.ShouldBeLessThanOrEqualTo(4_304);
        after.ShouldBeLessThanOrEqualTo(4_280);

        long Minimum()
        {
            Measure(100);
            long minimum = long.MaxValue;
            for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(1000)); }
            return minimum / 1000;
        }
        long Measure(int count)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < count; index++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                pending.IsCompletedSuccessfully.ShouldBeTrue();
                var disposal = ((DatabaseResultSet)pending.Result).DisposeAsync();
                disposal.IsCompletedSuccessfully.ShouldBeTrue();
                disposal.GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
    }
#endif

    private static SqlExpression Expression(string source)
        => new SqlQueryParser().Parse("SELECT " + source).ShouldBeOfType<SqlQueryStatement>()
            .SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression;
}
