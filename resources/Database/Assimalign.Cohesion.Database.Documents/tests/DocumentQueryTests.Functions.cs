using System;
using System.Collections.Generic;
using System.Globalization;
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
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentQueryTests
{
    public static IEnumerable<object?[]> ScalarCases => new object?[][]
    {
        ["LOWER(name)", "alpha"], ["UPPER(name)", "ALPHA"], ["LOWER(amount)", null],
        ["LENGTH('😀a')", 2L], ["SUBSTR('😀ab',1,1)", "a"], ["SUBSTR('😀ab',-1)", "b"],
        ["SUBSTR('abc',1e20)", ""], ["SUBSTR('abc',0,-1)", null], ["SUBSTR('abc',0.5)", null],
        ["POSITION('abc','z')", -1L], ["POSITION('😀ab','a')", 1L], ["POSITION('abc','')", 0L],
        ["CONTAINS('Alpha','a')", true], ["STARTS_WITH('Alpha','a')", false], ["ENDS_WITH('Alpha','a')", true],
        ["REPLACE('abcabc','b','X')", "aXcaXc"], ["REPLACE('abc','','X')", "abc"],
        ["REPLACE('abcabcabc','b','X',1)", "aXcabcabc"], ["REPLACE('abcabcabc','b','X',2)", "aXcaXcabc"],
        ["REPLACE('abcabc','b','X',0)", "abcabc"], ["REPLACE('abcabc','b','X',-2)", "aXcaXc"],
        ["REPLACE('abcabc','b','X',1e20)", "aXcaXc"], ["REPLACE('aaaa','aa','X',1)", "Xaa"],
        ["REPLACE('😀a😀a','😀','x',1)", "xa😀a"], ["REPLACE(name,'A','x',amount)", "xlpha"],
        ["REPLACE('abc','a','b',NULL)", null], ["REPLACE('abc','a','b',MISSING) IS MISSING", true],
        ["REPLACE('abc','a','b',name)", null],
        ["REPLACE('','x','y')", ""], ["TRIM(' abc ')", "abc"], ["TRIM('😀abc😀','😀')", "abc"],
        ["TRIM('\tabc\r\n')", "abc"], ["TRIM(' \t\n\f\rabc \t\n\f\r')", "abc"],
        ["TRIM('\vabc\u00a0')", "\vabc\u00a0"], ["TRIM('\tabc\t',' ')", "\tabc\t"],
        ["CONCAT(name,' ',name)", "Alpha Alpha"], ["ABS(-2)", 2m], ["ROUND(2.5)", 3m],
        ["ROUND(-2.5)", -3m], ["ROUND(125,-1)", 130m], ["ROUND(-125,-1)", -130m],
        ["ROUND(4.9999999999999999999999999999,-1)", 0m],
        ["ROUND(-4.9999999999999999999999999999,-1)", 0m],
        ["ROUND(2.5,29)", 2.5m], ["ROUND(123,-100)", 0m], ["TRUNC(2.567,2)", 2.56m],
        ["TRUNC(-2.567,2)", -2.56m], ["TRUNC(125,-1)", 120m], ["TRUNC(2.5,100)", 2.5m],
        ["TRUNC(4.9999999999999999999999999999,-1)", 0m],
        ["FLOOR(-2.5)", -3m], ["CEIL(-2.5)", -2m], ["POWER(2,3)", 8m], ["POWER(2,-2)", 0.25m],
        ["SQRT(4)", 2m], ["SQRT(2)", 1.4142135623730951m], ["POWER(2,0.5)", 1.4142135623730951m],
        ["STR_TO_MILLIS('1970-01-01T00:00:00Z')", 0L],
        ["STR_TO_MILLIS('1970-01-01T01:00:00+01:00')", 0L], ["STR_TO_MILLIS('1970-01-01')", 0L],
        ["STR_TO_MILLIS('1970-01-01T00:00:00.123Z')", 123L], ["STR_TO_MILLIS('no')", null],
        ["STR_TO_MILLIS('10/12/2020')", null], ["MILLIS_TO_STR(0)", "1970-01-01T00:00:00.000Z"],
        ["MILLIS_TO_STR(1e20)", null], ["MILLIS_TO_STR(-1e20)", null],
        ["MILLIS_TO_STR(STR_TO_MILLIS('2000-01-02T03:04:05.678Z'))", "2000-01-02T03:04:05.678Z"],
        ["TYPE(NULL)", "null"], ["TYPE(TRUE)", "boolean"], ["TYPE(1)", "number"], ["TYPE(name)", "string"],
        ["TYPE(tags)", "array"], ["TYPE({x:1})", "object"], ["TYPE(absent)", "missing"],
        ["IS_ARRAY(tags)", true], ["IS_OBJECT({x:1})", true], ["IS_STRING(name)", true],
        ["IS_NUMBER(amount)", true], ["IS_BOOLEAN(TRUE)", true], ["IS_ARRAY(name)", false],
        ["ARRAY_LENGTH(tags)", 2L], ["ARRAY_CONTAINS(tags,'blue')", true], ["ARRAY_CONTAINS(tags,'red')", false],
        ["ARRAY_LENGTH(amount)", null], ["IFNULL(NULL,0)", 0m], ["IFNULL(amount,0)", 2m],
        ["COALESCE(absent,NULL,1)", 1m], ["IFMISSING(absent,'x')", "x"], ["IFMISSING(NULL,'x')", null],
        ["IFMISSINGORNULL(absent,NULL,1)", 1m], ["IFMISSINGORNULL(NULL,MISSING)", null],
        ["IFNULL(absent,0) IS MISSING", true], ["IFMISSING(MISSING,MISSING)", null],
        ["LOWER(absent) IS MISSING", true], ["LOWER(NULL)", null], ["IS_NUMBER(MISSING) IS MISSING", true],
        ["CONCAT(NULL,MISSING) IS MISSING", true], ["COALESCE(1,SQRT(-1))", 1m],
        ["IFNULL(1,1/0)", 1m], ["IFMISSING(NULL,1/0)", null],
        ["LENGTH(name) + 1", 6m], ["-LENGTH(name)", -5m], ["SUM(LENGTH(name))", 5m],
        ["AVG(ARRAY_LENGTH(tags))", 2m], ["SUM(ABS(amount))", 2m], ["COUNT(*)", 1L], ["COUNT(*) + 1", 2m],
    };

    [Theory(DisplayName = "Cohesion Test [Documents] - Scalars: exact SQL++ results and unknown propagation")]
    [MemberData(nameof(ScalarCases))]
    public async Task Execute_ScalarFunction_ShouldReturnExactValueAsync(string expression, object? expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("scalars", new());
        var database = await engine.CreateDatabaseAsync("scalars", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"name\":\"Alpha\",\"amount\":2,\"tags\":[\"blue\",\"green\"]}");
        // Act
        var row = (await RowsAsync(session, $"SELECT {expression} FROM items")).ShouldHaveSingleItem();
        // Assert
        row.GetValue(0).ShouldBe(expected);
    }

    public static IEnumerable<object?[]> LargeStringCases
    {
        get
        {
            object?[][] cases =
            [
                ["LENGTH({0})", 1_048_577L], ["POSITION({0},'a')", 0L], ["POSITION({0},'z')", -1L],
                ["CONTAINS({0},'a')", true], ["CONTAINS({0},'z')", false],
                ["POSITION({0},{0})", 0L], ["CONTAINS({0},{0})", true],
                ["STARTS_WITH({0},{0})", true], ["ENDS_WITH({0},{0})", true],
                ["SUBSTR({0},0,1)", "a"], ["SUBSTR({0},1048576,1)", "a"], ["SUBSTR({0},-1)", "a"],
                ["LENGTH(SUBSTR({0},1))", 1_048_576L],
                ["TRIM({0},'a')", ""],
                ["REPLACE({0},'a','')", ""],
            ];
            foreach (var item in cases)
            {
                yield return [item[0], item[1], true];
                yield return [item[0], item[1], false];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Scalars: large parameter and stored inputs stream without a result-size refusal")]
    [MemberData(nameof(LargeStringCases))]
    public async Task Execute_LargeStringInput_ShouldLimitOnlyConstructedResultsAsync(string expression, object? expected, bool parameter)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("large-scalars", new());
        var database = await engine.CreateDatabaseAsync("scalars", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        string text = new('a', 1_048_577);
        await PutAsync(collection, session, "a", parameter ? "{}" : "{\"name\":\"" + text + "\"}");
        var parameters = new Dictionary<string, object?> { ["p"] = text };

        // Act
        var row = (await RowsAsync(session, "SELECT " + string.Format(CultureInfo.InvariantCulture, expression, parameter ? "$p" : "name") + " FROM items", parameters))
            .ShouldHaveSingleItem();

        // Assert
        row.GetValue(0).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Scalars: literal kinds fail during planning")]
    [InlineData("SELECT SQRT('x') FROM items")]
    [InlineData("SELECT SUBSTR(name,'a') FROM items")]
    [InlineData("SELECT REPLACE(name,'a','b','2') FROM items")]
    [InlineData("SELECT ARRAY_LENGTH({x:1}) FROM items")]
    [InlineData("UPDATE items SET n = SQRT('x')")]
    [InlineData("DELETE FROM items WHERE SUBSTR(name,'a')")]
    [InlineData("UPDATE items SET n = COUNT(*)")]
    [InlineData("INSERT INTO items (KEY,VALUE) VALUES ('x',COUNT(*))")]
    [InlineData("SELECT name FROM items WHERE COUNT(*) > 1")]
    [InlineData("DELETE FROM items WHERE COUNT(*) > 0")]
    [InlineData("SELECT SUM(ABS(COUNT(*))) FROM items")]
    [InlineData("SELECT name FROM items WHERE 1 IN (COUNT(*))")]
    public async Task Plan_InvalidFunctionContext_ShouldFailBeforeReadingAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("scalars", new());
        var database = await engine.CreateDatabaseAsync("scalars", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act / Assert
        (await Should.ThrowAsync<DatabaseException>(async () => await PlanAsync(database, query)))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC001");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Scalars: grouped calls retain the exact grouping diagnostic")]
    public async Task Plan_UngroupedScalarArgument_ShouldReportGroupingMessageAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("scalars", new());
        var database = await engine.CreateDatabaseAsync("scalars", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await PlanAsync(database, "SELECT LOWER(name) FROM items GROUP BY category"));

        // Assert
        const string message = "An OQL grouped query may only read grouping expressions outside aggregate calls.";
        error.Message.ShouldBe(message);
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC001");
        diagnostic.Message.ShouldBe(message);
        diagnostic.Start.ShouldBe(13);
        diagnostic.End.ShouldBe(17);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Scalars: domain and overflow errors are coded and abort explicit transactions")]
    [InlineData("SQRT(-1)", "COHDOC019", "The OQL function argument is outside its domain.")]
    [InlineData("POWER(-2,0.5)", "COHDOC019", "The OQL function argument is outside its domain.")]
    [InlineData("ROUND(1,0.5)", "COHDOC019", "The OQL function argument is outside its domain.")]
    [InlineData("POWER(10,100)", "COHDOC007", "OQL numbers must fit the decimal scalar domain.")]
    [InlineData("POWER(10,1000.5)", "COHDOC007", "OQL numbers must fit the decimal scalar domain.")]
    [InlineData("REPLACE('abc','a','b',0.5)", "COHDOC008", "The OQL REPLACE count must be an integer.")]
    [InlineData("ROUND(70000000000000000000000000000,-29)", "COHDOC007", "OQL numbers must fit the decimal scalar domain.")]
    public async Task Execute_FunctionFailure_ShouldAbortWithFixedDiagnosticAsync(string expression, string code, string message)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("scalars", new());
        var database = await engine.CreateDatabaseAsync("scalars", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY,VALUE) VALUES ('a',1)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, $"SELECT {expression} FROM items"));
        // Assert
        error.Message.ShouldBe(message);
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(7);
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Scalars: grouping, Int64 metadata and writes retain their contracts")]
    public async Task Execute_GroupedAndWrittenFunctions_ShouldKeepTypesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("scalars", new());
        var database = await engine.CreateDatabaseAsync("scalars", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"name\":\"Alpha\",\"category\":\"A\",\"tags\":[1,2]}");
        await PutAsync(collection, session, "b", "{\"name\":\"Beta\",\"category\":\"a\",\"tags\":[]}");
        // Act / Assert
        var grouped = (await RowsAsync(session, "SELECT LOWER(category) AS c, COUNT(*) AS n, UPPER(MIN(name)) AS first FROM items GROUP BY LOWER(category) HAVING COUNT(*) ORDER BY COUNT(*)")).ShouldHaveSingleItem();
        grouped.GetValue(0).ShouldBe("a"); grouped.GetValue(1).ShouldBe(2L); grouped.GetValue(2).ShouldBe("ALPHA");
        (await RowsAsync(session, "SELECT LOWER(name) FROM items WHERE LENGTH(name) ORDER BY LENGTH(name)")).Count.ShouldBe(2);
        var promoted = await RowsAsync(session, "SELECT CASE WHEN name = 'Alpha' THEN 1 ELSE LENGTH(name) END FROM items");
        promoted.Select(row => row.GetValue(0)).ShouldBe(new object?[] { 1m, 4m });
        foreach (string suffix in new[] { "", "WHERE FALSE GROUP BY category" })
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync($"SELECT COUNT(*) AS c, LENGTH('x') AS l, POSITION('x','y') AS p, ARRAY_LENGTH([]) AS a FROM items {suffix}", cancellationToken: CancellationToken.None);
            result.Columns.Select(column => column.Type).ShouldBe(Enumerable.Repeat(DatabaseType.Int64, 4));
        }
        await using (var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT VALUE COUNT(*) FROM items", cancellationToken: CancellationToken.None))
        {
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { System.Text.Encoding.UTF8.GetString(row.GetBytes(0).Span).ShouldBe("2"); }
        }
        await session.ExecuteAsync("UPDATE items SET n = LENGTH(name)", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT n FROM items ORDER BY n")).Select(row => row.GetInt64(0)).ShouldBe([4L, 5L]);
        await session.CreateCollectionAsync("copies", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO copies (KEY k, VALUE v) SELECT META().id AS k, {n:LENGTH(name)} AS v FROM items", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT n FROM copies ORDER BY n")).Select(row => row.GetInt64(0)).ShouldBe([4L, 5L]);
        DocumentPlanner.IsConstant(((OqlSelectExpression)
            ((OqlQueryStatement)new OqlQueryParser()
                .Parse("SELECT ABS(1) FROM items")).OqlExpression).Projections[0].Expression).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Scalars: string bounds and invariant culture")]
    public async Task Execute_BoundedInvariantFunctions_ShouldKeepLimitsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("limits", new());
        var database = await engine.CreateDatabaseAsync("limits", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY,VALUE) VALUES ('a',1)", cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["text"] = new string('a', 1_048_576) };
        // Act / Assert
        (await RowsAsync(session, "SELECT LENGTH(CONCAT($text,'')) FROM items", parameters)).Single().GetValue(0).ShouldBe(1_048_576L);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, "SELECT CONCAT($text,'a') FROM items", parameters));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC007");
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var row = (await RowsAsync(session, "SELECT LOWER('I') AS l, UPPER('i') AS u, MILLIS_TO_STR(0) AS d FROM items")).Single();
            row.GetValue(0).ShouldBe("i"); row.GetValue(1).ShouldBe("I"); row.GetValue(2).ShouldBe("1970-01-01T00:00:00.000Z");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Scalars: LENGTH observes cancellation after its parameter has evaluated")]
    public void Evaluate_LengthParameterCancels_ShouldObserveCancellationInsideLoop()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var parameters = new CancellingStringParameters(cancellation, new string('a', 1_048_576));
        var evaluator = new DocumentExpressionEvaluator(null, parameters, cancellation.Token);
        var call = new OqlCallExpression("LENGTH", [new OqlParameterExpression("p")]);
        cancellation.IsCancellationRequested.ShouldBeFalse();

        // Act / Assert
        Should.Throw<OperationCanceledException>(() => evaluator.Evaluate(call, default));
        parameters.ReadCount.ShouldBe(1);
        cancellation.IsCancellationRequested.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Scalars: constructed results still obey the UTF-16 cap")]
    [InlineData("LOWER($p)")]
    [InlineData("UPPER($p)")]
    [InlineData("SUBSTR($p,0)")]
    [InlineData("TRIM($p)")]
    [InlineData("REPLACE($p,'a','a')")]
    public void Evaluate_OversizeConstructedString_ShouldKeepResultCap(string expression)
    {
        // Arrange
        var parameters = new Dictionary<string, object?> { ["p"] = new string('a', 1_048_577) };
        var evaluator = new DocumentExpressionEvaluator(null, parameters, CancellationToken.None);
        var query = (OqlSelectExpression)((OqlQueryStatement)new OqlQueryParser().Parse("SELECT " + expression + " FROM items")).OqlExpression;

        // Act
        var error = Should.Throw<DatabaseException>(() => evaluator.Evaluate(query.Projections[0].Expression, default));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC007");
        error.Message.ShouldBe("The OQL function string exceeds 1,048,576 UTF-16 units.");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Clock: transaction start survives every row and ReadCommitted repinning")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task Execute_ClockFunctions_ShouldUseTransactionStartAsync(IsolationLevel isolation)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("clock", new());
        var database = await engine.CreateDatabaseAsync("clock", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY,VALUE) VALUES ('a',1),('b',2)", cancellationToken: CancellationToken.None);
        // Act / Assert
        for (int i = 0; i < 2; i++)
        {
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var instant = await ReadClockAsync();
            var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            instant.ShouldBeInRange(before, after);
            await Task.Delay(50, CancellationToken.None);
        }
        long beginBefore = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var transaction = await session.BeginTransactionAsync(isolation, CancellationToken.None);
        long beginAfter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long first = await ReadClockAsync();
        await Task.Delay(50, CancellationToken.None);
        (await ReadClockAsync()).ShouldBe(first);
        first.ShouldBeInRange(beginBefore, beginAfter);
        await transaction.RollbackAsync(CancellationToken.None);

        async Task<long> ReadClockAsync()
        {
            var rows = await RowsAsync(session, "SELECT NOW_MILLIS() AS a, NOW_MILLIS() AS b, NOW_STR() AS s FROM items");
            long first = rows[0].GetValue(0).ShouldBeOfType<long>();
            foreach (var row in rows)
            {
                row.GetValue(0).ShouldBe(first); row.GetValue(1).ShouldBe(first);
                row.GetString(2).ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(first).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            }
            return first;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Clock: the internal provider reaches SELECT, system sources, UPDATE, DELETE and INSERT SELECT")]
    public async Task Execute_InternalClockProvider_ShouldReachEveryExecutingEvaluatorAsync()
    {
        // Arrange
        var clock = new Assimalign.Cohesion.Database.Tests.ManualTimeProvider();
        await using var engine = DocumentDatabaseEngine.Create("clock-seam", new() { TimeProvider = clock });
        var database = await engine.CreateDatabaseAsync("clock", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.CreateCollectionAsync("copies", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY,VALUE) VALUES ('a',{name:'Alpha'})", cancellationToken: CancellationToken.None);
        long initial = clock.GetUtcNow().ToUnixTimeMilliseconds();
        // Act / Assert
        (await RowsAsync(session, "SELECT NOW_MILLIS() FROM items")).Single().GetValue(0).ShouldBe(initial);
        clock.Advance(TimeSpan.FromHours(3));
        long start = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var parameters = new Dictionary<string, object?> { ["start"] = start };
        await session.ExecuteAsync("UPDATE items SET instant = NOW_MILLIS() WHERE NOW_MILLIS() = $start", parameters, CancellationToken.None);
        (await RowsAsync(session, "SELECT instant FROM items")).Single().GetInt64(0).ShouldBe(start);
        await session.ExecuteAsync("INSERT INTO copies (KEY k, VALUE v) SELECT META().id AS k, {instant:NOW_MILLIS()} AS v FROM items", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT instant FROM copies")).Single().GetInt64(0).ShouldBe(start);
        foreach (var row in await RowsAsync(session, "SELECT NOW_MILLIS() FROM COHESION_SCHEMA.OBJECT_OWNERSHIP"))
        { row.GetValue(0).ShouldBe(start); }
        (await session.ExecuteAsync("DELETE FROM copies WHERE NOW_MILLIS() = $start", parameters, CancellationToken.None)).AffectedCount.ShouldBe(1);
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(session, "SELECT NOW_MILLIS() FROM items")).Single().GetValue(0).ShouldBe(clock.GetUtcNow().ToUnixTimeMilliseconds());
    }
}
